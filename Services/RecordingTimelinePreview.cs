using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Cut.Services;

internal static class RecordingTimelinePreview
{
    internal static async Task<BitmapSource> CreateAsync(MediaTools tools, RecordingClip clip, CancellationToken token)
    {
        string path = clip.Cache.FilePath("timeline-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            string filter = $"fps={MediaTools.Number(10 / clip.Seconds)}:start_time=0,scale=128:72:force_original_aspect_ratio=decrease,pad=128:72:(ow-iw)/2:(oh-ih)/2,tile=10x1";
            await tools.RunAsync(new[] { "-i", clip.PreviewPath, "-vf", filter, "-frames:v", "1", "-threads", "2", "-filter_threads", "2", "-update", "1", path }, token);
            token.ThrowIfCancellationRequested();
            using var stream = File.OpenRead(path);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); bitmap.Freeze(); return bitmap;
        }
        finally { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
