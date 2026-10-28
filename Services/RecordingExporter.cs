using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cut.Services;

internal enum RecordingFormat { Gif, Mp4 }
internal sealed record RecordingExportOptions(RecordingFormat Format, int Fps, double Scale, double Start, double End, bool Loop = true, bool HighQuality = true)
{
    internal void Validate(RecordingClip clip)
    {
        if (!Enum.IsDefined(Format) || !RecordingOptions.FrameRates.Contains(Fps) || Fps > clip.Options.Fps ||
            Scale is not (1 or .75 or .5) || !double.IsFinite(Start) || !double.IsFinite(End) || Start < 0 ||
            End > clip.Seconds + .000001 || End - Start < 1.0 / clip.Options.Fps - .000001)
            throw new ArgumentException("请检查导出帧率、尺寸及起止时间，至少保留一帧。");
    }
}

internal static class RecordingExporter
{
    private static List<string> Input(RecordingClip clip) => new() { "-protocol_whitelist", "file,pipe", "-f", "concat", "-safe", "0", "-i", clip.ListPath };
    internal static string Filter(RecordingClip clip, RecordingExportOptions options)
    {
        int w = Math.Max(2, (int)Math.Round(clip.Options.Area.Width * options.Scale));
        int h = Math.Max(2, (int)Math.Round(clip.Options.Area.Height * options.Scale));
        // First fill timestamp gaps between paused segments and the static tail,
        // then trim. Re-timing never depends on the number of captured frames.
        return $"fps={clip.Options.Fps}:start_time=0,tpad=stop_mode=clone:stop_duration=60,trim=duration={MediaTools.Number(clip.Seconds)}," +
            $"trim=start={MediaTools.Number(options.Start)}:end={MediaTools.Number(options.End)},setpts=PTS-STARTPTS," +
            $"fps={options.Fps},scale={w}:{h}:flags=lanczos";
    }
    internal static async Task PreviewAsync(MediaTools tools, RecordingClip clip, IProgress<double>? progress, CancellationToken token)
    {
        var options = new RecordingExportOptions(RecordingFormat.Mp4, clip.Options.Fps, 1, 0, clip.Seconds);
        var args = Input(clip);
        args.AddRange(new[] { "-vf", Filter(clip, options) + ",pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p", "-an", "-c:v", "libx264",
            "-preset", "veryfast", "-crf", "18", "-threads", "4", "-filter_threads", "2", "-movflags", "+faststart", clip.PreviewPath });
        await RunAsync(tools, args, clip.Seconds, progress, 0, 1, token);
    }
    internal static async Task ExportAsync(MediaTools tools, RecordingClip clip, RecordingExportOptions options, string destination, IProgress<double>? progress, CancellationToken token)
    {
        options.Validate(clip);
        string target = Path.GetFullPath(destination);
        if (target.StartsWith(clip.Cache.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择录制缓存目录以外的保存位置。");
        string extension = options.Format == RecordingFormat.Gif ? ".gif" : ".mp4";
        if (!Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件扩展名与导出格式不一致。");
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".Cut-export-" + Guid.NewGuid().ToString("N") + extension);
        string palette = clip.Cache.FilePath("palette-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            double duration = options.End - options.Start;
            var args = Input(clip);
            string filter = Filter(clip, options);
            if (options.Format == RecordingFormat.Gif)
            {
                args.AddRange(new[] { "-vf", filter + $",palettegen=max_colors={(options.HighQuality ? 256 : 128)}:stats_mode=full", "-frames:v", "1", "-threads", "2", "-filter_threads", "2", "-update", "1", palette });
                await RunAsync(tools, args, duration, progress, 0, .4, token);
                args = Input(clip);
                args.AddRange(new[] { "-i", palette, "-filter_complex", $"[0:v]{filter}[v];[v][1:v]paletteuse=dither={(options.HighQuality ? "sierra2_4a" : "bayer:bayer_scale=3")}:diff_mode=rectangle[out]",
                    "-map", "[out]", "-an", "-loop", options.Loop ? "0" : "-1", "-threads", "2", "-filter_complex_threads", "2", "-fps_mode", "vfr", temporary });
                await RunAsync(tools, args, duration, progress, .4, .6, token);
            }
            else
            {
                args.AddRange(new[] { "-vf", filter + ",pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p", "-an", "-c:v", "libx264", "-preset", "fast",
                    "-crf", options.HighQuality ? "18" : "23", "-threads", "4", "-filter_threads", "2", "-movflags", "+faststart", temporary });
                await RunAsync(tools, args, duration, progress, 0, 1, token);
            }
            await tools.DurationAsync(temporary, token);
            token.ThrowIfCancellationRequested();
            // Only replace an existing destination after a complete, readable encode.
            File.Move(temporary, target, true); progress?.Report(1);
        }
        finally
        {
            try { File.Delete(temporary); File.Delete(palette); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private static async Task RunAsync(MediaTools tools, IEnumerable<string> args, double duration, IProgress<double>? progress, double start, double span, CancellationToken token)
    {
        using var child = tools.Start(args);
        var wait = child.WaitAsync(token);
        try
        {
            while (!wait.IsCompleted)
            {
                progress?.Report(start + span * Math.Clamp(child.Progress.Seconds / Math.Max(.01, duration), 0, .99));
                await Task.WhenAny(wait, Task.Delay(150, token)); token.ThrowIfCancellationRequested();
            }
            if (await wait != 0) throw new IOException("导出失败，录制内容已保留：" + child.Error);
            progress?.Report(start + span);
        }
        catch { child.Kill(); try { await wait; } catch { } throw; }
    }
}
