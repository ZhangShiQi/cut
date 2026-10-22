using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class ScrollWebTests
{
    public static async Task<int> RunAsync()
    {
        string folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "paldb");
        var log = new StringBuilder("Paldb browser screenshots: known DOM scroll offsets, real page pixels; no input/network during this test.\n");
        ScrollCaptureWindow? window = null;
        try
        {
            var paths = Directory.GetFiles(folder, "user-*.png").OrderBy(p => int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[1])).ToArray();
            if (paths.Length < 3) throw new InvalidOperationException("At least three captured page frames are required.");
            var frames = paths.Select(path =>
            {
                using var stream = File.OpenRead(path);
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze();
                return ScrollFrame.From(new CroppedBitmap(image, new Int32Rect(0, 57, image.PixelWidth, image.PixelHeight - 57)));
            }).ToArray();
            int[] offsets = paths.Select(p => int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[1])).ToArray();
            bool failedBefore = false;
            var result = new ScrollAssembler(frames[0], ScrollDirection.Down);
            for (int i = 1; i < frames.Length; i++)
            {
                // The browser tool rescales viewport screenshots. Unlike production GDI
                // frames, a CSS scroll offset need not be an integer raster displacement.
                int expected = (int)Math.Round((offsets[i] - offsets[i - 1]) * (frames[i].Height + 57) / 866.0);
                var old = ScrollMatcher.MatchPixels(frames[i - 1], frames[i], ScrollDirection.Down);
                failedBefore |= old.Kind == ScrollMatchKind.Uncertain;
                var diagnostic = ScrollMatcher.MatchFeatures(frames[i - 1], frames[i], ScrollDirection.Down, s => log.AppendLine(s));
                var match = ScrollMatcher.Match(frames[i - 1], frames[i], ScrollDirection.Down);
                log.AppendLine($"{offsets[i - 1]}->{offsets[i]}: old={old}; fallback={diagnostic}; new={match}");
                if (match.Kind != ScrollMatchKind.Matched || Math.Abs(match.Distance - expected) > 1) throw new InvalidOperationException("Incorrect web displacement");
                result.Append(frames[i], match.Distance);
                var up = ScrollMatcher.Match(frames[i], frames[i - 1], ScrollDirection.Up);
                if (up.Kind != ScrollMatchKind.Matched || Math.Abs(up.Distance - expected) > 1) throw new InvalidOperationException("Incorrect upward web displacement: " + up);
            }
            if (!failedBefore) throw new InvalidOperationException("The supplied frames did not reproduce the original failure.");
            int expectedHeight = frames[0].Height + (int)Math.Round((offsets[^1] - offsets[0]) * (frames[0].Height + 57) / 866.0);
            if (Math.Abs(result.Height - expectedHeight) > frames.Length - 1) throw new InvalidOperationException("Incorrect output height");
            ExportService.SavePng(result.Build(), Path.Combine(folder, "stitched-fixed.png"));
            log.AppendLine("PASS original failure reproduced; both directions agree with known DOM offsets within 1 raster pixel per tool-rescaled frame; output height and PNG export");
            foreach (var direction in Enum.GetValues<ScrollDirection>())
            {
                var ordered = direction == ScrollDirection.Down ? frames : frames.Reverse().ToArray();
                int index = 0; long clock = 0;
                window = new(new(20, 20, frames[0].Width, frames[0].Height), new(0, 0, 3000, 2000))
                {
                    AutoScroll = true, Direction = direction, AutomaticPolling = false,
                    ValidateForTest = () => true, SkipAutoGuardsForTest = true, CountdownMilliseconds = 0,
                    Now = () => clock, Capture = _ => ordered[index].Image(),
                    SendWheel = _ => { index = Math.Min(index + 1, ordered.Length - 1); return true; }
                };
                window.Show(); await window.ToggleAsync();
                for (int poll = 0; poll < 160 && window.Running; poll++) { clock += 180; await window.PollAsync(); }
                if (window.Running || !window.Message.Contains("未检测到新内容") || window.Document?.Appends != frames.Length - 1)
                    throw new InvalidOperationException("Web automatic replay did not finish: " + window.Message);
                var reference = new ScrollAssembler(ordered[0], direction);
                for (int i = 1; i < ordered.Length; i++) reference.Append(ordered[i], ScrollMatcher.Match(ordered[i - 1], ordered[i], direction).Distance);
                ScrollTests.Same(window.Document.Build(), reference.Build(), "Automatic replay output " + direction);
                await window.CompleteAsync();
                log.AppendLine("PASS real website frames through automatic controller: " + direction + ", exact replay output, boundary stop and completion (injected input)");
            }
            log.AppendLine("ALL WEB SCROLL CHECKS PASSED");
            File.WriteAllText(Path.Combine(folder, "web-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex) { window?.Close(); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "web-test.txt"), log + ex.ToString()); return 1; }
    }
}
