using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace Cut.Core;

internal static partial class ScrollMatcher
{
    // A translucent page over a fixed wallpaper has two different motions. Whole-overlap
    // pixel equality cannot describe it. Use unique, mutually matched foreground features
    // only as a fallback, still rejecting ambiguous shifts and unexplained large changes.
    internal static ScrollMatch MatchFeatures(ScrollFrame previous, ScrollFrame current, ScrollDirection direction, Action<string>? trace = null, int featureCount = 2400)
    {
        if (previous.Width != current.Width || previous.Height != current.Height || current.Width < 96 || current.Height < 120)
            return new(ScrollMatchKind.Uncertain);
        int w = current.Width, h = current.Height, maximum = h - Math.Max(80, h / 4);
        using var oldGray = Gray(previous); using var newGray = Gray(current);
        using var orb = ORB.Create(nFeatures: featureCount, nLevels: 1, edgeThreshold: 16, fastThreshold: 12);
        using var oldDescriptors = new Mat(); using var newDescriptors = new Mat();
        orb.DetectAndCompute(oldGray, null, out var oldPoints, oldDescriptors);
        orb.DetectAndCompute(newGray, null, out var newPoints, newDescriptors);
        if (oldDescriptors.Empty() || newDescriptors.Empty()) return new(ScrollMatchKind.Uncertain);
        using var matcher = new BFMatcher(NormTypes.Hamming);
        var forward = matcher.KnnMatch(newDescriptors, oldDescriptors, 2);
        var backward = matcher.KnnMatch(oldDescriptors, newDescriptors, 2);
        var motions = new List<(int Distance, int X, int Y)>();
        int stationaryForeground = 0;
        foreach (var pair in forward)
        {
            if (pair.Length < 2 || pair[0].Distance >= pair[1].Distance * .72 || pair[0].Distance > 45) continue;
            var best = pair[0]; var reverse = backward[best.TrainIdx];
            if (reverse.Length < 2 || reverse[0].TrainIdx != best.QueryIdx || reverse[0].Distance >= reverse[1].Distance * .72) continue;
            var a = oldPoints[best.TrainIdx].Pt; var b = newPoints[best.QueryIdx].Pt;
            if (Math.Abs(a.X - b.X) > .5) continue;
            int d = (int)Math.Round(direction == ScrollDirection.Down ? a.Y - b.Y : b.Y - a.Y);
            // Keep opposite/oversize movements in the vote count so a weak accidental
            // match cannot override the actual movement of most of the page.
            if (d != 0) motions.Add((d, (int)b.X, (int)b.Y));
            else if (newGray.At<byte>((int)b.Y, (int)b.X) > 90) stationaryForeground++;
        }
        var groups = motions.GroupBy(p => p.Distance).OrderByDescending(g => g.Count()).ToArray();
        trace?.Invoke($"features {oldPoints.Length}/{newPoints.Length}; stationary foreground={stationaryForeground}; votes " + string.Join(", ", groups.Take(8).Select(g => $"{g.Key}:{g.Count()}")));
        if (groups.Length == 0) return new(ScrollMatchKind.Uncertain);
        // Fractional browser/DPI rasterization can put the same motion in adjacent pixel
        // bins. Treat these as one cluster, but never merge distinct card-row periods.
        int center = groups.OrderByDescending(g => motions.Count(p => Math.Abs(p.Distance - g.Key) <= 1)).First().Key;
        var winner = motions.Where(p => Math.Abs(p.Distance - center) <= 1).ToArray();
        int distance = (int)Math.Round(winner.Average(p => p.Distance));
        int runnerUp = groups.Where(g => Math.Abs(g.Key - center) > 2)
            .Select(g => motions.Count(p => Math.Abs(p.Distance - center) > 2 && Math.Abs(p.Distance - g.Key) <= 1)).DefaultIfEmpty().Max();
        if (distance < 1 || distance > maximum || winner.Length < 16 || winner.Length < motions.Count * .65 ||
            winner.Length < runnerUp * 3 || winner.Length < stationaryForeground) return new(ScrollMatchKind.Uncertain);
        var cells = winner.Select(p => (X: p.X * 4 / w, Y: p.Y * 4 / h)).Distinct().ToArray();
        trace?.Invoke($"cells={cells.Length}, x={cells.Select(c => c.X).Distinct().Count()}, y={cells.Select(c => c.Y).Distinct().Count()}");
        if (cells.Length < 4 || cells.Select(c => c.X).Distinct().Count() < 2 || cells.Select(c => c.Y).Distinct().Count() < 2)
            return new(ScrollMatchKind.Uncertain);
        if (winner.Max(p => p.Y) - winner.Min(p => p.Y) < (h - distance) * .4 ||
            winner.Max(p => p.X) - winner.Min(p => p.X) < w * .25) return new(ScrollMatchKind.Uncertain);
        if (HasFixedEdge(previous, current, true) || HasFixedEdge(previous, current, false))
            return new(ScrollMatchKind.Uncertain, Hint: "选区顶部或底部包含固定栏，请用“排除顶部 / 底部”去掉固定栏后重新采集");
        int overlap = h - distance, oldY = direction == ScrollDirection.Down ? distance : 0, newY = direction == ScrollDirection.Down ? 0 : distance;
        long unexplained = 0, samples = 0; var backgroundCells = new HashSet<(int, int)>();
        int step = Math.Max(1, w / 500);
        for (int y = 0; y < overlap; y += 2)
        for (int x = 6; x < w - 6; x += step)
        {
            int p = ((oldY + y) * w + x) * 4, q = ((newY + y) * w + x) * 4, stationary = q;
            int movedError = ColorError(previous.Pixels, p, current.Pixels, q);
            int fixedError = ColorError(previous.Pixels, stationary, current.Pixels, q);
            if (movedError > 64 && fixedError > 12) unexplained++;
            if (fixedError <= 3 && movedError > 8) backgroundCells.Add((x * 4 / w, y * 4 / overlap));
            samples++;
        }
        trace?.Invoke($"unexplained={(double)unexplained / samples:P2}; backgroundCells={backgroundCells.Count}");
        if (unexplained > samples * .025) return new(ScrollMatchKind.Uncertain, Hint: "部分内容仍在加载或发生变化，请等待页面稳定后继续");
        return new(ScrollMatchKind.Matched, distance, Layered: backgroundCells.Count >= 6);
    }

    private static int ColorError(byte[] a, int p, byte[] b, int q) =>
        Math.Max(Math.Abs(a[p] - b[q]), Math.Max(Math.Abs(a[p + 1] - b[q + 1]), Math.Abs(a[p + 2] - b[q + 2])));

    private static bool HasFixedEdge(ScrollFrame previous, ScrollFrame current, bool top)
    {
        int w = current.Width, h = current.Height, sameRows = 0, bright = 0;
        for (int row = 0; row < Math.Min(100, h / 4); row++)
        {
            int y = top ? row : h - 1 - row, changed = 0, count = 0, detail = 0;
            for (int x = 6; x < w - 6; x += 3)
            {
                int p = (y * w + x) * 4;
                if (ColorError(previous.Pixels, p, current.Pixels, p) > 8) changed++;
                if (Math.Max(current.Pixels[p], Math.Max(current.Pixels[p + 1], current.Pixels[p + 2])) > 90 &&
                    ColorError(current.Pixels, p, current.Pixels, p - 4) > 20) detail++;
                count++;
            }
            if (changed > count * .01) break;
            sameRows++; bright += detail;
        }
        return sameRows >= 12 && bright >= 12;
    }
}
