using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;

namespace Cut;

internal static class ScrollFeatureTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Run()
    {
        // Unique icons across four columns, like a catalog over a fixed wallpaper.
        // Repeated rows without distinguishing features are tested as ambiguous elsewhere.
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawImage(ScrollTests.Fixture(800, 2400), new Rect(0, 0, 800, 2400));
            var random = new Random(735);
            for (int y = 0; y < 2400; y += 46)
            for (int column = 0; column < 4; column++)
            for (int i = 0; i < 20; i++)
            {
                int x = 25 + column * 190 + random.Next(80);
                var color = Color.FromRgb((byte)random.Next(140), (byte)random.Next(140), (byte)random.Next(140));
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(x, y + random.Next(30), 5 + random.Next(9), 5 + random.Next(9)));
            }
        }
        var source = new RenderTargetBitmap(800, 2400, 96, 96, PixelFormats.Pbgra32); source.Render(drawing); source.Freeze();
        ScrollFrame Layered(int offset)
        {
            var foreground = ScrollFrame.From(new CroppedBitmap(source, new Int32Rect(0, offset, 800, 600)));
            var pixels = new byte[foreground.Pixels.Length];
            for (int y = 0; y < 600; y++)
            for (int x = 0; x < 800; x++)
            {
                double background = 35 + 18 * Math.Sin(x / 39.0) + 14 * Math.Cos(y / 21.0) + 8 * Math.Sin((x + y) / 53.0);
                int p = (y * 800 + x) * 4;
                for (int c = 0; c < 3; c++) pixels[p + c] = (byte)Math.Clamp((255 - foreground.Pixels[p + c]) * .85 + background, 0, 255);
            }
            return new(800, 600, pixels);
        }
        var previous = Layered(200); int offset = 200; bool oldFailed = false;
        foreach (int step in new[] { 90, 180, 1, 120, 240, 60 })
        {
            var next = Layered(offset + step);
            oldFailed |= ScrollMatcher.MatchPixels(previous, next, ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain;
            var trace = new System.Text.StringBuilder();
            var match = ScrollMatcher.MatchFeatures(previous, next, ScrollDirection.Down, s => trace.AppendLine(s));
            Check(match.Kind == ScrollMatchKind.Matched && match.Distance == step, $"Layered down {step}: {match}\n{trace}");
            var up = ScrollMatcher.Match(next, previous, ScrollDirection.Up);
            Check(up.Kind == ScrollMatchKind.Matched && up.Distance == step, $"Layered up {step}: {up}");
            Check(ScrollMatcher.Match(next, previous, ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Layered reverse rejected");
            offset += step; previous = next;
        }
        Check(oldFailed, "Layered regression must exercise the former failure");
        Check(ScrollMatcher.Match(Layered(0), Layered(1000), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Layered no-overlap rejected");
        var baseline = ScrollFrame.From(new CroppedBitmap(source, new Int32Rect(0, 0, 800, 600)));
        byte[] animated = (byte[])baseline.Pixels.Clone();
        // A moving banner inside an otherwise stationary page is not a page scroll.
        for (int y = 80; y < 180; y++)
            Buffer.BlockCopy(baseline.Pixels, ((y + 30) * 800 + 20) * 4, animated, (y * 800 + 20) * 4, 600 * 4);
        Check(ScrollMatcher.Match(baseline, new(800, 600, animated), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Local animated widget rejected");
    }
}
