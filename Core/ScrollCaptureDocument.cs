using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;

namespace Cut.Core;

// Full, uncompressed source frames stay in a session-owned delete-on-close file.
// Only the last full frame and small preview are retained in memory. All I/O and
// final composition are called by the capture worker, never by the UI timer.
internal sealed class ScrollCaptureDocument : IDisposable
{
    internal const long DefaultCacheLimit = 1024L * 1024 * 1024;
    private sealed record Entry(long Offset, int Distance);
    private readonly List<Entry> entries = new();
    private readonly FileStream cache;
    private readonly long cacheLimit;
    private readonly object sync = new();
    private bool disposed;
    private BitmapSource thumbnail;
    public ScrollFrame Last { get; private set; }
    public ScrollDirection Direction { get; }
    public int Width => Last.Width;
    public int Height { get; private set; }
    public int Appends => entries.Count - 1;
    internal string CachePath { get; }
    internal long CachedBytes { get { lock (sync) return cache.Length; } }

    public ScrollCaptureDocument(ScrollFrame first, ScrollDirection direction, long cacheLimit = DefaultCacheLimit)
    {
        if (first.Height > ScrollAssembler.MaxHeight || (long)first.Width * first.Height > ScrollAssembler.MaxPixels)
            throw new InvalidOperationException("选区过大。");
        this.cacheLimit = cacheLimit; Last = first; Direction = direction; Height = first.Height;
        CachePath = Path.Combine(Path.GetTempPath(), "Cut-scroll-" + Guid.NewGuid().ToString("N") + ".cache");
        cache = new(CachePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
        try { entries.Add(new(Store(first), 0)); thumbnail = Preview(first); }
        catch { cache.Dispose(); throw; }
    }
    private long Store(ScrollFrame frame)
    {
        if (cache.Length + frame.Pixels.Length > cacheLimit) throw new IOException("原图缓存达到 1 GB 上限，请完成后分段截图。");
        long offset = cache.Length; cache.Position = offset;
        try { cache.Write(frame.Pixels); }
        catch { cache.SetLength(offset); throw; }
        return offset;
    }
    // A cached frame is not committed until the caller rechecks its session generation.
    internal long Cache(ScrollFrame frame)
    {
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return Store(frame); }
    }
    internal bool CanAppend(int distance) => Height + distance <= ScrollAssembler.MaxHeight &&
        (long)Width * (Height + distance) <= ScrollAssembler.MaxPixels && Appends < 1000;
    internal bool Commit(ScrollFrame frame, int distance, long offset)
    {
        if (frame.Width != Width || frame.Height != Last.Height || distance <= 0 || distance >= frame.Height)
            throw new ArgumentException("Invalid scroll frame");
        if (!CanAppend(distance)) return false;
        entries.Add(new(offset, distance)); Height += distance; Last = frame; thumbnail = Preview(frame); return true;
    }
    internal bool Undo()
    {
        lock (sync)
        {
            if (Appends == 0) return false;
            var removed = entries[^1]; entries.RemoveAt(entries.Count - 1); Height -= removed.Distance;
            Last = Read(entries[^1]); thumbnail = Preview(Last);
            cache.SetLength(entries[^1].Offset + Last.Pixels.Length); return true;
        }
    }
    private ScrollFrame Read(Entry entry)
    {
        var pixels = new byte[Last.Pixels.Length]; cache.Position = entry.Offset; cache.ReadExactly(pixels);
        return new(Width, Last.Height, pixels);
    }
    // Compose each selected strip directly into the final allocation. Overlap remains
    // available in the cache until this operation and the editor handoff have completed.
    internal BitmapSource Build(CancellationToken cancellation = default, Action<int>? progress = null)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var output = new byte[checked(Width * Height * 4)]; int row = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                int index = Direction == ScrollDirection.Down ? i : entries.Count - 1 - i;
                var entry = entries[index]; int count = index == 0 ? Last.Height : entry.Distance;
                int start = index == 0 || Direction == ScrollDirection.Up ? 0 : Last.Height - count;
                cache.Position = entry.Offset + (long)start * Width * 4;
                cache.ReadExactly(output.AsSpan(row * Width * 4, count * Width * 4)); row += count;
                progress?.Invoke((i + 1) * 100 / entries.Count);
            }
            cancellation.ThrowIfCancellationRequested();
            return new ScrollFrame(Width, Height, output).Image();
        }
    }
    private static BitmapSource Preview(ScrollFrame frame)
    {
        double scale = Math.Min(1, 160.0 / Math.Max(frame.Width, frame.Height));
        int width = Math.Max(1, (int)(frame.Width * scale)), height = Math.Max(1, (int)(frame.Height * scale));
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            Buffer.BlockCopy(frame.Pixels, ((int)(y / scale) * frame.Width + (int)(x / scale)) * 4, pixels, (y * width + x) * 4, 4);
        return new ScrollFrame(width, height, pixels).Image();
    }
    internal BitmapSource Thumbnail() => thumbnail;
    public void Dispose() { lock (sync) { if (disposed) return; disposed = true; cache.Dispose(); } }
}
