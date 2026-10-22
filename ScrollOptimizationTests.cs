using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class ScrollOptimizationTests
{
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static ScrollFrame Crop(BitmapSource source, int y, int height = 400) =>
        ScrollFrame.From(new CroppedBitmap(source, new Int32Rect(0, y, source.PixelWidth, height)));
    internal static async Task<int> RunAsync()
    {
        var log = new StringBuilder("Scroll optimization: generated pixels, disk cache, injected clock/input.\n");
        ScrollCaptureWindow? window = null;
        try
        {
            var source = ScrollTests.Fixture(640, 2600);
            await Task.Run(() =>
            {
                foreach (var direction in Enum.GetValues<ScrollDirection>())
                {
                    int[] offsets = direction == ScrollDirection.Down ? new[] { 0, 90, 330, 550 } : new[] { 550, 330, 90, 0 };
                    string path;
                    using (var document = new ScrollCaptureDocument(Crop(source, offsets[0]), direction))
                    {
                        path = document.CachePath;
                        for (int i = 1; i < offsets.Length; i++)
                        {
                            var next = Crop(source, offsets[i]); long cached = document.Cache(next);
                            Check(document.Commit(next, Math.Abs(offsets[i] - offsets[i - 1]), cached), "Cache commit");
                        }
                        Check(document.CachedBytes == 4L * 640 * 400 * 4, "Full overlapping originals retained");
                        ScrollTests.Same(document.Build(), Crop(source, 0, 950).Image(), "Deferred exact composition " + direction);
                        var last = document.Last;
                        document.Cache(Crop(source, 1000));
                        ScrollTests.Same(document.Build(), Crop(source, 0, 950).Image(), "Uncommitted cache is excluded");
                        Check(document.Undo(), "Undo cached frame");
                        ScrollTests.Same(document.Last.Image(), Crop(source, offsets[^2]).Image(), "Undo restores original full frame");
                        Check(document.CachedBytes == 3L * 640 * 400 * 4, "Undo reclaims discarded cache tail");
                        Check(document.Commit(last, Math.Abs(offsets[^1] - offsets[^2]), document.Cache(last)), "Continue after undo");
                        using var cancel = new CancellationTokenSource(); bool cancelled = false;
                        try { document.Build(cancel.Token, _ => cancel.Cancel()); } catch (OperationCanceledException) { cancelled = true; }
                        Check(cancelled, "Composition cancellation");
                        ScrollTests.Same(document.Build(), Crop(source, 0, 950).Image(), "Retry composition preserves originals");
                    }
                    Check(!File.Exists(path), "Cache deleted on disposal");
                }
                using var limited = new ScrollCaptureDocument(Crop(source, 0), ScrollDirection.Down, 640L * 400 * 4);
                bool full = false;
                try { limited.Cache(Crop(source, 90)); } catch (IOException) { full = true; }
                Check(full && limited.Appends == 0, "Cache limit preserves accepted frames");
                ScrollTests.Same(limited.Build(), Crop(source, 0).Image(), "Cache failure still permits completion");
            });
            log.AppendLine("PASS full originals, both directions exact pixels, discarded frame exclusion, undo/resume, cache quota, cancellation/retry and cleanup");
            foreach (var speed in Enum.GetValues<ScrollSpeed>())
            foreach (int height in new[] { 400, 800 })
            {
                var pace = new AutoScrollPacer(speed); long clock = 0;
                int initial = pace.WheelMagnitude;
                for (int i = 0; i < 10; i++)
                {
                    int sent = pace.WheelMagnitude; pace.InputSent(clock);
                    int distance = (int)Math.Round(sent * 1.5);
                    pace.Stable(clock += 1500, distance, height);
                    Check(pace.WheelMagnitude <= sent * 1.5 + 1, "Bounded calibration increase");
                }
                Check(Math.Abs(pace.WheelMagnitude * 1.5 - height * pace.TargetFraction) < 3, "Converges to height target " + speed + "/" + height);
                Check(pace.WheelMagnitude > initial, "Small steps grow");
                int beforeTail = pace.WheelMagnitude; pace.InputSent(clock); pace.Stable(clock + 1500, 1, height);
                Check(pace.WheelMagnitude == beforeTail, "One-pixel tail does not accelerate");
                pace.InputSent(clock + 2000); pace.Stable(clock + 3500, (int)(height * .72), height);
                Check(pace.WheelMagnitude < beforeTail, "Oversized shift reduces next input");
            }
            log.AppendLine("PASS adaptive height targets at 400/800px, three speeds, gradual increase, overshoot reduction and final remainder");
            foreach (var direction in Enum.GetValues<ScrollDirection>())
            {
                long clock = 0; int y = direction == ScrollDirection.Down ? 0 : 2200;
                var inputs = new List<int>();
                window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
                {
                    AutoScroll = true, Direction = direction, AutomaticPolling = false, CountdownMilliseconds = 0,
                    ValidateForTest = () => true, SkipAutoGuardsForTest = true, Now = () => clock,
                    Capture = _ => Crop(source, y).Image(),
                    SendWheel = delta => { inputs.Add(Math.Abs(delta)); y = Math.Clamp(y - (int)Math.Round(delta * 1.5), 0, 2200); return true; }
                };
                window.Show(); await window.ToggleAsync();
                for (int i = 0; i < 220 && window.Running; i++) { clock += 100; await window.PollAsync(); }
                Check(!window.Running && window.Message.Contains("未检测到新内容"), "Adaptive boundary stop: " + window.Message);
                Check(inputs.Max() > inputs[0], "Controller adapts actual inputs");
                Check(window.Document?.Appends < 20, "Fewer frames for same extent");
                string cachePath = window.Document!.CachePath;
                await window.CompleteAsync(); ScrollTests.Same(window.Result!, source, "Adaptive full output " + direction);
                for (int i = 0; i < 50 && File.Exists(cachePath); i++) await Task.Delay(20);
                Check(!File.Exists(cachePath), "Complete cleans session cache");
            }
            log.AppendLine("PASS adaptive controller both directions, fewer than 20 appends across 2200px, exact full output and completion cleanup");
            long now = 0; int position = 0;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gate = new ManualResetEventSlim(false);
            window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
            {
                AutoScroll = true, AutomaticPolling = false, CountdownMilliseconds = 0, ValidateForTest = () => true,
                SkipAutoGuardsForTest = true, Now = () => now, Capture = _ => Crop(source, position).Image(),
                SendWheel = _ => { position += 100; return true; },
                MatchForTest = (a, b, d) => { entered.TrySetResult(); gate.Wait(TimeSpan.FromSeconds(5)); return ScrollMatcher.Match(a, b, d); }
            };
            window.Show(); await window.ToggleAsync();
            for (int i = 0; i < 5; i++) { now += 180; await window.PollAsync(); }
            var pending = window.PollAsync(); await entered.Task;
            Check(window.InputsSent == 1, "No speculative input while background match is blocked");
            await window.Dispatcher.InvokeAsync(() => Check(window.Running, "Dispatcher responsive during match"));
            await window.ToggleAsync(); gate.Set(); await pending;
            Check(window.Document?.Appends == 0 && window.InputsSent == 1, "Paused matching result cannot commit or send input");
            await window.CompleteAsync();
            log.AppendLine("PASS bounded background work, responsive dispatcher, pause invalidation and no late input");
            now = 0; position = 0;
            window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
            {
                AutoScroll = true, AutomaticPolling = false, CountdownMilliseconds = 0, ValidateForTest = () => true,
                SkipAutoGuardsForTest = true, Now = () => now, Capture = _ => Crop(source, position).Image(),
                SendWheel = _ => { position += 100; return true; },
                MatchForTest = (_, _, _) => throw new IOException("Injected worker failure")
            };
            window.Show(); await window.ToggleAsync();
            for (int i = 0; i < 6; i++) { now += 180; await window.PollAsync(); }
            Check(!window.Running && window.InputsSent == 1, "Worker failure pauses input");
            await window.CompleteAsync();
            ScrollTests.Same(window.Result!, Crop(source, 0).Image(), "Worker failure permits accepted-frame completion");
            log.AppendLine("PASS worker failure preserves accepted originals and completion remains available");
            log.AppendLine("ALL SCROLL OPTIMIZATION CHECKS PASSED");
            Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/scroll-optimization-test.txt", log.ToString()); return 0;
        }
        catch (Exception ex) { window?.Close(); Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/scroll-optimization-test.txt", log + ex.ToString()); return 1; }
    }

    private sealed record Measurement(double Total, double Matching, double Caching, double Composition, BitmapSource Image);
    private static Measurement Measure(ScrollFrame[] frames, bool optimized)
    {
        var total = Stopwatch.StartNew(); var stage = new Stopwatch(); double matching = 0, caching = 0;
        var session = new ScrollMatchingSession();
        ScrollAssembler? legacy = null; ScrollCaptureDocument? document = null;
        try
        {
            stage.Start();
            if (optimized) document = new(frames[0], ScrollDirection.Down); else legacy = new(frames[0], ScrollDirection.Down);
            stage.Stop(); caching += stage.Elapsed.TotalMilliseconds;
            for (int i = 1; i < frames.Length; i++)
            {
                stage.Restart();
                var match = optimized ? session.Match(frames[i - 1], frames[i], ScrollDirection.Down) : ScrollMatcher.Match(frames[i - 1], frames[i], ScrollDirection.Down);
                stage.Stop(); matching += stage.Elapsed.TotalMilliseconds;
                Check(match.Kind == ScrollMatchKind.Matched, "Benchmark match failed at frame " + i);
                stage.Restart();
                if (optimized) document!.Commit(frames[i], match.Distance, document.Cache(frames[i]));
                else { legacy!.Append(frames[i], match.Distance); legacy.Thumbnail(); }
                stage.Stop(); caching += stage.Elapsed.TotalMilliseconds;
            }
            stage.Restart(); var result = optimized ? document!.Build() : legacy!.Build(); stage.Stop(); total.Stop();
            return new(total.Elapsed.TotalMilliseconds, matching, caching, stage.Elapsed.TotalMilliseconds, result);
        }
        finally { document?.Dispose(); }
    }
    internal static ScrollFrame[] LoadFrames(string directory, string pattern) => Directory.GetFiles(directory, pattern)
        .OrderBy(path => int.Parse(Path.GetFileNameWithoutExtension(path).Split('-')[1])).Select(path =>
        {
            using var stream = File.OpenRead(path); var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze();
            return ScrollFrame.From(new CroppedBitmap(image, new Int32Rect(0, 57, image.PixelWidth, image.PixelHeight - 57)));
        }).ToArray();
    internal static async Task<int> BenchmarkAsync()
    {
        var log = new StringBuilder("Website benchmark on paldb.cn/pals; actual recorded pixels, offline processing timings, not live browser end-to-end time.\n");
        try
        {
            await Task.Run(() =>
            {
                var frames = LoadFrames("artifacts/paldb", "user-*.png");
                Check(frames.Length == 8, "Expected eight original reference frames");
                Measure(frames, false); Measure(frames, true); // warm both paths before alternating runs
                var before = new List<Measurement>(); var after = new List<Measurement>();
                for (int i = 0; i < 7; i++)
                {
                    if (i % 2 == 0) { before.Add(Measure(frames, false)); after.Add(Measure(frames, true)); }
                    else { after.Add(Measure(frames, true)); before.Add(Measure(frames, false)); }
                    ScrollTests.Same(after[^1].Image, before[^1].Image, "Identical website pixels across old/new eight-frame pipeline");
                }
                static double Median(IEnumerable<double> values) => values.Order().ElementAt(3);
                foreach (var (label, data) in new[] { ("0.10.1 algorithm", before), ("0.11.0 cached pipeline", after) })
                    log.AppendLine($"{label}: median of 7 runs total={Median(data.Select(x => x.Total)):F1}ms, match={Median(data.Select(x => x.Matching)):F1}ms, cache/append/preview={Median(data.Select(x => x.Caching)):F1}ms, final compose={Median(data.Select(x => x.Composition)):F1}ms");
                log.AppendLine($"Same-frame processing reduction: {(1 - Median(after.Select(x => x.Total)) / Median(before.Select(x => x.Total))):P1}; all output pixels identical.");
                ExportService.SavePng(after[^1].Image, "artifacts/paldb/optimized-same-frames.png");
                // Compare identical document extent using larger steps with retained overlap.
                var wide = new[] { frames[0], frames[1], frames[4], frames[6], frames[7] };
                var wideResult = Measure(wide, true);
                Check(Math.Abs(wideResult.Image.PixelHeight - after[^1].Image.PixelHeight) <= 4, "Wide-step extent agrees within rescaled frame rounding");
                ExportService.SavePng(wideResult.Image, "artifacts/paldb/optimized-wide-steps.png");
                log.AppendLine($"Larger-step replay: 5 rather than 8 frames for the same 1020 CSS-pixel extent, processing={wideResult.Total:F1}ms, output={wideResult.Image.PixelWidth}x{wideResult.Image.PixelHeight}. This is a sampled replay, not a live adaptive wheel speed measurement.");
                if (Directory.Exists("artifacts/paldb-adaptive"))
                {
                    var fresh = LoadFrames("artifacts/paldb-adaptive", "frame-*.png");
                    var paths = Directory.GetFiles("artifacts/paldb-adaptive", "frame-*.png").OrderBy(p => int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[1])).ToArray();
                    var offsets = paths.Select(p => int.Parse(Path.GetFileNameWithoutExtension(p).Split('-')[1])).ToArray();
                    Check(fresh.Length >= 6, "Fresh website samples missing");
                    foreach (var direction in Enum.GetValues<ScrollDirection>())
                    {
                        var session = new ScrollMatchingSession();
                        for (int i = 1; i < fresh.Length; i++)
                        {
                            var match = direction == ScrollDirection.Down ? session.Match(fresh[i - 1], fresh[i], direction) : session.Match(fresh[i], fresh[i - 1], direction);
                            int expected = (int)Math.Round((offsets[i] - offsets[i - 1]) * (fresh[0].Height + 57) / 866.0);
                            Check(match.Kind == ScrollMatchKind.Matched && Math.Abs(match.Distance - expected) <= 1, $"Fresh web displacement {offsets[i - 1]}->{offsets[i]} {direction}: {match}");
                        }
                    }
                    var freshResult = Measure(fresh, true);
                    ExportService.SavePng(freshResult.Image, "artifacts/paldb-adaptive/optimized.png");
                    log.AppendLine($"PASS fresh browser frames at CSS offsets {string.Join(", ", offsets)}; both directions within 1 raster pixel per pair; final {freshResult.Image.PixelWidth}x{freshResult.Image.PixelHeight}.");
                }
                log.AppendLine("ALL WEBSITE BENCHMARK CHECKS PASSED");
            });
            File.WriteAllText("artifacts/scroll-benchmark.txt", log.ToString()); return 0;
        }
        catch (Exception ex) { File.WriteAllText("artifacts/scroll-benchmark.txt", log + ex.ToString()); return 1; }
    }
}
