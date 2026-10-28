using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cut.Core;

namespace Cut.Services;

internal sealed record RecordingOptions(PixelRect Area, PixelRect Monitor, int Fps = 30, bool Cursor = true)
{
    internal static readonly int[] FrameRates = { 5, 10, 15, 30, 60 };
    internal const double MaximumSeconds = 60;
    internal void Validate()
    {
        if (!FrameRates.Contains(Fps)) throw new ArgumentException("请选择 5 / 10 / 15 / 30 / 60 FPS。");
        if (Area.Width < 16 || Area.Height < 16 || Area.Intersect(Monitor) != Area)
            throw new ArgumentException("请在同一显示器内框选至少 16 × 16 像素的区域。");
        if ((long)Area.Width * Area.Height > 8_294_400) throw new ArgumentException("录制区域最多支持 829 万像素，请缩小选区。");
    }
}

internal enum RecordingState { Ready, Starting, Recording, Paused, Finishing, Finished, Disposed }

internal sealed class RecordingCache : IDisposable
{
    internal const long MaximumBytes = 1L << 30;
    private const string Prefix = "Cut-recording-";
    private FileStream? lease;
    internal string DirectoryPath { get; }
    internal RecordingCache()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        lease = new FileStream(Path.Combine(DirectoryPath, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(Path.Combine(DirectoryPath, ".cut-recording"), "Cut recording cache v1");
    }
    internal string FilePath(string name) => Path.Combine(DirectoryPath, name);
    internal long Bytes => Directory.Exists(DirectoryPath) ? Directory.EnumerateFiles(DirectoryPath).Sum(p => { try { return new FileInfo(p).Length; } catch (IOException) { return 0; } }) : 0;
    internal void EnsureSpace()
    {
        if (Bytes >= MaximumBytes) throw new IOException("录制缓存已达到 1 GiB 上限，请完成已有录制。");
        var drive = new DriveInfo(Path.GetPathRoot(DirectoryPath)!);
        if (drive.AvailableFreeSpace < 128L * 1024 * 1024) throw new IOException("临时磁盘空间不足，请先完成已有录制。");
    }
    internal static void CleanupOrphans()
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
                TryDelete(directory, checkLease: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static void TryDelete(string directory, bool checkLease)
    {
        try
        {
            string full = Path.GetFullPath(directory), name = Path.GetFileName(full);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[Prefix.Length..], "N", out _) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(full).Any() ||
                !File.Exists(Path.Combine(full, ".cut-recording"))) return;
            if (checkLease) { using var guard = new FileStream(Path.Combine(full, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            foreach (string file in Directory.EnumerateFiles(full))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(file);
            }
            Directory.Delete(full);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose() { lease?.Dispose(); lease = null; TryDelete(DirectoryPath, checkLease: true); }
}

internal sealed record RecordingClip(RecordingOptions Options, RecordingCache Cache, string ListPath, string PreviewPath, double Seconds);

// Pauses close a segment. Resume opens a new segment, so time spent paused or
// initializing a capture backend is never included in the output timeline.
internal sealed class RecordingSession : IDisposable
{
    private readonly MediaTools tools;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<(string Name, double Seconds)> segments = new();
    private readonly Stopwatch activeTime = new();
    private MediaProcess? encoder;
    private string? pending;
    private double completedSeconds;
    private double initialSeconds;
    private bool disposed;
    internal RecordingOptions Options { get; }
    internal RecordingCache Cache { get; }
    internal RecordingState State { get; private set; } = RecordingState.Ready;
    internal string Backend { get; private set; } = "Windows Graphics Capture";
    internal string? BackendNotice { get; private set; }
    internal Func<double>? ActiveElapsedForTest { get; set; }
    private double ActiveElapsed => ActiveElapsedForTest?.Invoke() ?? initialSeconds + activeTime.Elapsed.TotalSeconds;
    internal double Seconds => Math.Min(RecordingOptions.MaximumSeconds, completedSeconds + (State == RecordingState.Recording ? ActiveElapsed : 0));
    internal MediaProgress Progress => encoder?.Progress ?? new(0, 0, 0);
    internal bool EncoderExited => encoder?.HasExited == true;
    internal int SegmentCount => segments.Count;
    internal bool ForceGdi { get; set; }
    internal string? SyntheticInput { get; set; }
    internal RecordingSession(MediaTools tools, RecordingOptions options) { options.Validate(); this.tools = tools; Options = options; Cache = new(); }

    internal async Task ResumeAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(linked.Token);
        try
        {
            ThrowIfDisposed();
            if (State is not (RecordingState.Ready or RecordingState.Paused)) return;
            if (completedSeconds >= RecordingOptions.MaximumSeconds - 1.0 / Options.Fps) throw new InvalidOperationException("已达到 60 秒上限。");
            Cache.EnsureSpace(); State = RecordingState.Starting;
            pending = Cache.FilePath($"segment-{segments.Count:D4}.mkv");
            try
            {
                bool gdi = ForceGdi || Backend == "GDI";
                try { await StartEncoderAsync(gdi, linked.Token); }
                catch (Exception ex) when (!gdi && SyntheticInput is null && ex is not OperationCanceledException)
                {
                    encoder?.Dispose(); encoder = null;
                    BackendNotice = "Windows Graphics Capture 不可用，已回退 GDI；高帧率实际效果取决于机器性能。";
                    await StartEncoderAsync(true, linked.Token);
                }
                initialSeconds = encoder!.Progress.Seconds; activeTime.Restart(); State = RecordingState.Recording;
            }
            catch { State = RecordingState.Paused; encoder?.Dispose(); encoder = null; throw; }
        }
        finally { gate.Release(); }
    }
    private async Task StartEncoderAsync(bool gdi, CancellationToken token)
    {
        encoder = tools.Start(CaptureArguments(Options, pending!, RecordingOptions.MaximumSeconds - completedSeconds,
            Math.Max(1, RecordingCache.MaximumBytes - Cache.Bytes), gdi, SyntheticInput));
        var exit = encoder.WaitAsync();
        var ready = await Task.WhenAny(encoder.FirstFrame, exit, Task.Delay(4500, token));
        token.ThrowIfCancellationRequested();
        if (ready != encoder.FirstFrame || encoder.HasExited) throw new IOException("无法获取实时画面。" + encoder.Error);
        Backend = SyntheticInput is not null ? "合成测试" : gdi ? "GDI" : "Windows Graphics Capture";
    }
    internal static IEnumerable<string> CaptureArguments(RecordingOptions options, string path, double remaining, long bytes, bool gdi, string? synthetic = null)
    {
        var args = new List<string>();
        if (synthetic is not null) args.AddRange(new[] { "-re", "-f", "lavfi", "-i", synthetic });
        else if (gdi) args.AddRange(new[] { "-probesize", "32", "-analyzeduration", "0", "-f", "gdigrab", "-framerate", options.Fps.ToString(), "-draw_mouse", options.Cursor ? "1" : "0",
            "-offset_x", options.Area.X.ToString(), "-offset_y", options.Area.Y.ToString(), "-video_size", $"{options.Area.Width}x{options.Area.Height}", "-i", "desktop" });
        else
        {
            var handle = NativeMethods.MonitorFromPoint(new() { X = options.Area.X, Y = options.Area.Y }, 2);
            string source = $"gfxcapture=hmonitor={handle.ToInt64()}:max_framerate={options.Fps}:capture_cursor={(options.Cursor ? 1 : 0)}:display_border=0" +
                $":crop_left={options.Area.X - options.Monitor.X}:crop_top={options.Area.Y - options.Monitor.Y}" +
                $":crop_right={options.Monitor.Right - options.Area.Right}:crop_bottom={options.Monitor.Bottom - options.Area.Bottom},hwdownload,format=bgra[v]";
            args.AddRange(new[] { "-filter_complex", source, "-map", "[v]" });
        }
        args.AddRange(new[] { "-an", "-t", MediaTools.Number(remaining), "-fs", bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-crf", "12", "-pix_fmt", "yuv444p", "-fps_mode", "vfr", "-threads", "4", path });
        return args;
    }
    internal async Task PauseAsync()
    {
        await gate.WaitAsync(lifetime.Token);
        try { ThrowIfDisposed(); await PauseCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task PauseCoreAsync()
    {
        if (State != RecordingState.Recording || encoder is null) return;
        double elapsed = ActiveElapsed; activeTime.Stop();
        State = RecordingState.Paused;
        var child = encoder; encoder = null;
        try
        {
            await child.StopAsync();
            double mediaSeconds = await tools.DurationAsync(pending!, lifetime.Token);
            double duration = Math.Min(RecordingOptions.MaximumSeconds - completedSeconds, Math.Max(1.0 / Options.Fps, Math.Max(elapsed, mediaSeconds)));
            segments.Add((Path.GetFileName(pending!), duration)); completedSeconds += duration;
        }
        finally { child.Dispose(); pending = null; activeTime.Reset(); }
    }
    internal async Task<RecordingClip> FinishAsync(IProgress<double>? progress = null)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            ThrowIfDisposed(); await PauseCoreAsync();
            if (segments.Count == 0) throw new InvalidOperationException("尚未录到画面，请先开始录制。");
            State = RecordingState.Finishing;
            string list = Cache.FilePath("recording.ffconcat"), preview = Cache.FilePath("preview.mp4");
            // Generated names contain no user input or quoting characters.
            var text = new StringBuilder("ffconcat version 1.0\n");
            foreach (var segment in segments) text.Append("file '").Append(segment.Name).Append("'\nduration ").Append(MediaTools.Number(segment.Seconds)).Append('\n');
            await File.WriteAllTextAsync(list, text.ToString(), new UTF8Encoding(false), lifetime.Token);
            var clip = new RecordingClip(Options, Cache, list, preview, Math.Min(60, completedSeconds));
            await RecordingExporter.PreviewAsync(tools, clip, progress, lifetime.Token);
            State = RecordingState.Finished; return clip;
        }
        catch { if (!disposed) State = RecordingState.Paused; throw; }
        finally { gate.Release(); }
    }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(RecordingSession)); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; State = RecordingState.Disposed; lifetime.Cancel();
        encoder?.Kill();
        // Wait off the dispatcher for an in-flight start/export/stop before deleting files.
        _ = Task.Run(async () => { await gate.WaitAsync(); try { encoder?.Dispose(); encoder = null; Cache.Dispose(); } finally { gate.Release(); lifetime.Dispose(); } });
    }
}
