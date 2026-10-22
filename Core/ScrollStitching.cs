using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace Cut.Core;

internal enum ScrollDirection { Down, Up }
internal enum ScrollMatchKind { Same, Matched, Uncertain }
internal sealed record ScrollMatch(ScrollMatchKind Kind, int Distance = 0);
internal sealed class ScrollFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public ScrollFrame(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != checked(width * height * 4)) throw new ArgumentException("Invalid frame");
        Width = width; Height = height; Pixels = pixels;
    }
    public static ScrollFrame From(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        converted.CopyPixels(pixels, source.PixelWidth * 4, 0); return new(source.PixelWidth, source.PixelHeight, pixels);
    }
    public BitmapSource Image()
    {
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgr32, null, Pixels, Width * 4); image.Freeze(); return image;
    }
    public ScrollFrame Rows(int start, int count)
    {
        var pixels = new byte[checked(Width * count * 4)];
        Buffer.BlockCopy(Pixels, start * Width * 4, pixels, 0, pixels.Length); return new(Width, count, pixels);
    }
}
internal static class ScrollMatcher
{
    // Ignore isolated cursor/animation pixels, but never infer motion from an unchanged frame.
    public static bool Stable(ScrollFrame a, ScrollFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        long error = 0; int changed = 0, samples = 0;
        int step = Math.Max(1, a.Width / 350);
        for (int y = 0; y < a.Height; y += 2)
        for (int x = 0; x < a.Width; x += step)
        {
            int p = (y * a.Width + x) * 4;
            int d = Math.Max(Math.Abs(a.Pixels[p] - b.Pixels[p]), Math.Max(Math.Abs(a.Pixels[p + 1] - b.Pixels[p + 1]), Math.Abs(a.Pixels[p + 2] - b.Pixels[p + 2])));
            error += d; if (d > 18) changed++; samples++;
        }
        return samples > 0 && (double)error / samples < .65 && (double)changed / samples < .003;
    }
    private static Mat Gray(ScrollFrame frame)
    {
        using var rgba = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.Pixels);
        var gray = new Mat(); Cv2.CvtColor(rgba, gray, ColorConversionCodes.BGRA2GRAY); return gray;
    }
    public static ScrollMatch Match(ScrollFrame previous, ScrollFrame current, ScrollDirection direction)
    {
        if (previous.Width != current.Width || previous.Height != current.Height) return new(ScrollMatchKind.Uncertain);
        if (Stable(previous, current)) return new(ScrollMatchKind.Same);
        int h = current.Height, w = current.Width, band = Math.Clamp(h / 10, 24, 80);
        int maximum = h - Math.Max(80, h / 4);
        if (maximum < 2 || w < 96) return new(ScrollMatchKind.Uncertain);
        using var oldGray = Gray(previous); using var newGray = Gray(current);
        var votes = new Dictionary<int, HashSet<int>>();
        // Three independent horizontal strips supply candidate shifts. Search the allowed direction only.
        for (int strip = 0; strip < 3; strip++)
        for (int anchor = 0; anchor < 2; anchor++)
        {
            int left = 8 + strip * (w - 24) / 3, width = Math.Max(16, (w - 24) / 3);
            int top = direction == ScrollDirection.Down ? anchor * band : h - (anchor + 1) * band;
            using var template = new Mat(newGray, new Rect(left, top, width, band));
            Cv2.MeanStdDev(template, out _, out Scalar deviation);
            if (deviation.Val0 < 6) continue;
            int searchTop = direction == ScrollDirection.Down ? top : top - maximum;
            using var search = new Mat(oldGray, new Rect(left, searchTop, width, maximum + band));
            using var correlation = new Mat();
            Cv2.MatchTemplate(search, template, correlation, TemplateMatchModes.CCoeffNormed);
            int rows = correlation.Rows;
            for (int peak = 0; peak < 24; peak++)
            {
                Cv2.MinMaxLoc(correlation, out _, out double score, out _, out Point location);
                if (!double.IsFinite(score) || score < .975) break;
                int displacement = searchTop + location.Y - top;
                int distance = direction == ScrollDirection.Down ? displacement : -displacement;
                if (distance >= 2 && distance <= maximum)
                {
                    if (!votes.TryGetValue(distance, out var supporters)) votes[distance] = supporters = new();
                    supporters.Add(strip * 2 + anchor);
                }
                for (int y = Math.Max(0, location.Y - 1); y <= Math.Min(rows - 1, location.Y + 1); y++) correlation.Set(y, 0, -1f);
            }
        }
        var accepted = new List<(int Distance, double Error)>();
        foreach (var vote in votes.Where(v => v.Value.Count >= 2))
        {
            int d = vote.Key, overlap = h - d;
            int oldY = direction == ScrollDirection.Down ? d : 0;
            int newY = direction == ScrollDirection.Down ? 0 : d;
            long error = 0; int changed = 0, count = 0;
            int xStep = Math.Max(1, w / 450);
            // Verify the entire overlap, rather than trusting a repeated heading or a single matching patch.
            for (int y = 0; y < overlap; y += 2)
            for (int x = 6; x < w - 6; x += xStep)
            {
                int p = ((oldY + y) * w + x) * 4, q = ((newY + y) * w + x) * 4;
                int delta = Math.Max(Math.Abs(previous.Pixels[p] - current.Pixels[q]), Math.Max(Math.Abs(previous.Pixels[p + 1] - current.Pixels[q + 1]), Math.Abs(previous.Pixels[p + 2] - current.Pixels[q + 2])));
                error += delta; if (delta > 18) changed++; count++;
            }
            double mean = (double)error / Math.Max(1, count), fraction = (double)changed / Math.Max(1, count);
            if (mean < 2 && fraction < .012) accepted.Add((d, mean));
        }
        var ordered = accepted.OrderBy(x => x.Error).ToArray();
        if (ordered.Length == 0 || (ordered.Length > 1 && ordered[1].Error - ordered[0].Error < .7)) return new(ScrollMatchKind.Uncertain);
        return new(ScrollMatchKind.Matched, ordered[0].Distance);
    }
}
internal sealed class ScrollAssembler
{
    public const int MaxHeight = 30000;
    public const long MaxPixels = 32000000;
    private readonly List<ScrollFrame> chunks = new();
    private readonly int viewportHeight;
    public ScrollDirection Direction { get; }
    public ScrollFrame Last { get; private set; }
    public int Width => Last.Width;
    public int Height { get; private set; }
    public int Appends => chunks.Count - 1;
    public ScrollAssembler(ScrollFrame first, ScrollDirection direction)
    {
        if (first.Height > MaxHeight || (long)first.Width * first.Height > MaxPixels) throw new InvalidOperationException("选区过大。");
        Direction = direction; Last = first; viewportHeight = Height = first.Height; chunks.Add(first);
    }
    public bool Append(ScrollFrame frame, int distance)
    {
        if (frame.Width != Width || frame.Height != viewportHeight || distance <= 0 || distance >= viewportHeight) throw new ArgumentException("Invalid scroll distance");
        if (Height + distance > MaxHeight || (long)Width * (Height + distance) > MaxPixels || Appends >= 1000) return false;
        var strip = Direction == ScrollDirection.Down ? frame.Rows(frame.Height - distance, distance) : frame.Rows(0, distance);
        if (Direction == ScrollDirection.Down) chunks.Add(strip); else chunks.Insert(0, strip);
        Last = frame; Height += distance; return true;
    }
    public bool Undo()
    {
        if (Appends == 0) return false;
        int index = Direction == ScrollDirection.Down ? chunks.Count - 1 : 0;
        Height -= chunks[index].Height; chunks.RemoveAt(index);
        Last = ReadRows(Direction == ScrollDirection.Down ? Height - viewportHeight : 0, viewportHeight);
        return true;
    }
    private ScrollFrame ReadRows(int start, int count)
    {
        var data = new byte[checked(Width * count * 4)];
        int position = 0;
        foreach (var chunk in chunks)
        {
            int top = Math.Max(start, position), bottom = Math.Min(start + count, position + chunk.Height);
            if (bottom > top) Buffer.BlockCopy(chunk.Pixels, (top - position) * Width * 4, data, (top - start) * Width * 4, (bottom - top) * Width * 4);
            position += chunk.Height;
        }
        return new(Width, count, data);
    }
    public BitmapSource Build() => ReadRows(0, Height).Image();
    public BitmapSource Thumbnail()
    {
        double scale = Math.Min(1, Math.Min(160.0 / Width, 160.0 / Height));
        int w = Math.Max(1, (int)(Width * scale)), h = Math.Max(1, (int)(Height * scale));
        byte[] data = new byte[w * h * 4]; int chunkIndex = 0, start = 0;
        for (int y = 0; y < h; y++)
        {
            int sourceY = Math.Min(Height - 1, (int)(y / scale));
            while (sourceY >= start + chunks[chunkIndex].Height) { start += chunks[chunkIndex].Height; chunkIndex++; }
            for (int x = 0; x < w; x++)
            {
                int p = ((sourceY - start) * Width + Math.Min(Width - 1, (int)(x / scale))) * 4;
                Buffer.BlockCopy(chunks[chunkIndex].Pixels, p, data, (y * w + x) * 4, 4);
            }
        }
        return new ScrollFrame(w, h, data).Image();
    }
}
