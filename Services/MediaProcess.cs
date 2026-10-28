using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Cut.Services;

internal sealed record MediaProgress(double Seconds, int Frames, double Fps);

// No shell, PATH lookup or user-provided filter expressions. Both pipes are drained
// concurrently; cancellation and parent-process death terminate the encoder.
internal sealed class MediaProcess : IDisposable
{
    private readonly Process process;
    private readonly SafeFileHandle job;
    private readonly Task outputPump, errorPump;
    private readonly StringBuilder output = new(), errors = new();
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private double seconds, fps;
    private int frames;
    internal MediaProgress Progress { get; private set; } = new(0, 0, 0);
    internal bool HasExited => process.HasExited;
    internal Task FirstFrame => firstFrame.Task;
    internal string Output => output.ToString();
    internal string Error { get { lock (errors) return errors.ToString(); } }

    internal MediaProcess(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        process = new Process { StartInfo = start };
        job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimits(); limits.Basic.Flags = 0x2000; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { job.Dispose(); throw new System.ComponentModel.Win32Exception(); }
        try
        {
            if (!process.Start()) throw new IOException("无法启动本地编码器。");
            if (!AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception();
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } job.Dispose(); process.Dispose(); throw; }
        outputPump = PumpOutputAsync(); errorPump = PumpErrorsAsync();
    }
    private async Task PumpOutputAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is string line)
        {
            if (output.Length < 2 * 1024 * 1024) output.AppendLine(line);
            int split = line.IndexOf('='); if (split < 0) continue;
            var key = line[..split]; var value = line[(split + 1)..];
            if (key == "out_time_us" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double microseconds)) seconds = Math.Max(0, microseconds / 1_000_000);
            if (key == "frame" && int.TryParse(value, out int count)) frames = count;
            if (key == "fps" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate)) fps = rate;
            if (key == "progress") { Progress = new(seconds, frames, fps); if (frames > 0) firstFrame.TrySetResult(); }
        }
    }
    private async Task PumpErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync() is string line)
            lock (errors) { errors.AppendLine(line); if (errors.Length > 12000) errors.Remove(0, errors.Length - 8000); }
    }
    internal async Task<int> WaitAsync(CancellationToken token = default)
    {
        using var registration = token.Register(Kill);
        await process.WaitForExitAsync();
        await Task.WhenAll(outputPump, errorPump);
        token.ThrowIfCancellationRequested(); return process.ExitCode;
    }
    internal async Task StopAsync()
    {
        if (!process.HasExited)
        {
            try { await process.StandardInput.WriteLineAsync("q"); await process.StandardInput.FlushAsync(); } catch (IOException) { } catch (InvalidOperationException) { }
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(exited, Task.Delay(6000)) != exited) Kill();
        }
        await WaitAsync();
    }
    internal void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
    public void Dispose() { Kill(); job.Dispose(); process.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}

internal sealed class MediaTools
{
    internal string Encoder { get; }
    internal string Probe { get; }
    private MediaTools(string root) { Encoder = Path.Combine(root, "ffmpeg.exe"); Probe = Path.Combine(root, "ffprobe.exe"); }
    internal static async Task<MediaTools> LoadAsync(CancellationToken token = default)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "MediaTools", "ffmpeg");
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "manifest.json"), token));
            foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                string name = entry.GetProperty("file").GetString()!;
                if (name is not ("ffmpeg.exe" or "ffprobe.exe")) throw new IOException("工具清单无效。");
                await using var stream = File.OpenRead(Path.Combine(root, name));
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new IOException("录制工具校验失败：" + name);
            }
            return new(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { throw new IOException("录制组件缺失或损坏，请使用完整发布包；源码构建请运行 Tools/download-recording-tools.ps1。", ex); }
    }
    internal MediaProcess Start(IEnumerable<string> args) => new(Encoder, new[] { "-hide_banner", "-loglevel", "warning", "-y", "-stats_period", "0.2", "-progress", "pipe:1" }.Concat(args));
    internal async Task<string> RunAsync(IEnumerable<string> args, CancellationToken token = default)
    {
        using var child = Start(args);
        if (await child.WaitAsync(token) != 0) throw new IOException("本地编码失败：" + child.Error);
        return child.Output;
    }
    internal async Task<double> DurationAsync(string path, CancellationToken token = default)
    {
        using var child = new MediaProcess(Probe, new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path });
        if (await child.WaitAsync(token) != 0 || !double.TryParse(child.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration) || !double.IsFinite(duration) || duration <= 0)
            throw new IOException("录制文件没有可读取的画面。" + child.Error);
        return duration;
    }
    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
