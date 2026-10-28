using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class RecordingTests
{
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static string Output => Path.Combine(Environment.CurrentDirectory, "artifacts", "recording");
    private static RecordingOptions Options(int fps = 15) => new(new(20, 20, 321, 181), new(0, 0, 1600, 1000), fps, false);
    private static async Task<JsonDocument> ProbeAsync(MediaTools tools, string file)
    {
        using var process = new MediaProcess(tools.Probe, new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", file });
        Check(await process.WaitAsync() == 0, "probe: " + process.Error);
        return JsonDocument.Parse(process.Output);
    }
    private static async Task WaitDeletedAsync(string directory)
    {
        for (int i = 0; i < 100 && Directory.Exists(directory); i++) await Task.Delay(30);
        Check(!Directory.Exists(directory), "cache cleanup: " + directory);
    }
    private static void Render(Window window, string name)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); ExportService.SavePng(image, Path.Combine(Output, name));
    }
    private static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    internal static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Output);
        var log = new StringBuilder("Cut recording tests: generated media and synthetic UI only; no user screen/clipboard/network/settings changes.\n");
        try
        {
            var tools = await MediaTools.LoadAsync();
            Check(RecordingOptions.FrameRates.SequenceEqual(new[] { 5, 10, 15, 30, 60 }), "five rates");
            foreach (var invalid in new[] { Options() with { Fps = 24 }, Options() with { Area = new(-10, 0, 100, 100) }, Options() with { Area = new(0, 0, 1, 1) } })
            { bool rejected = false; try { invalid.Validate(); } catch (ArgumentException) { rejected = true; } Check(rejected, "invalid recording options"); }
            var negative = new RecordingOptions(new(-1500, -600, 640, 360), new(-1920, -1080, 1920, 1080), 60);
            negative.Validate();
            string gdi = string.Join(" ", RecordingSession.CaptureArguments(negative, "test.mkv", 60, 1024, true));
            Check(gdi.Contains("-offset_x -1500") && gdi.Contains("-offset_y -600") && gdi.Contains("-framerate 60") && gdi.Contains("-t 60"), "physical negative coordinates and capture cap");
            log.AppendLine("PASS options, all five rates, single-monitor/size validation, negative physical coordinates, native 60s command limit.");

            foreach (int fps in RecordingOptions.FrameRates)
            {
                var session = new RecordingSession(tools, Options(fps)) { SyntheticInput = $"testsrc=size=321x181:rate={fps}" };
                string cache = session.Cache.DirectoryPath;
                try
                {
                    await session.ResumeAsync(); await Task.Delay(650); await session.PauseAsync();
                    Check(session.State == RecordingState.Paused && session.SegmentCount == 1 && session.Seconds > 0, "capture " + fps);
                    double paused = session.Seconds; await Task.Delay(150); Check(session.Seconds == paused, "paused time excluded " + fps);
                    using var metadata = await ProbeAsync(tools, session.Cache.FilePath("segment-0000.mkv"));
                    var stream = metadata.RootElement.GetProperty("streams")[0];
                    Check(stream.GetProperty("width").GetInt32() == 321 && stream.GetProperty("height").GetInt32() == 181, "odd size preserved " + fps);
                    Check(stream.GetProperty("codec_name").GetString() == "h264", "compressed capture " + fps);
                }
                finally { session.Dispose(); }
                await WaitDeletedAsync(cache);
            }
            log.AppendLine("PASS actual FFmpeg capture and compressed cache at 5/10/15/30/60 FPS; odd physical dimensions preserved, pause clock stable, cache removed.");

            var editing = new RecordingSession(tools, Options()) { SyntheticInput = "color=red:size=322x182:rate=15,crop=321:181:0:0:exact=1", ActiveElapsedForTest = () => 3 };
            string editingCache = editing.Cache.DirectoryPath;
            try
            {
                await editing.ResumeAsync(); await Task.Delay(500); await editing.PauseAsync();
                Check(Math.Abs(editing.Seconds - 3) < .01, "first segment duration: " + editing.Seconds);
                await Task.Delay(400); Check(Math.Abs(editing.Seconds - 3) < .01, "pause does not extend timeline");
                editing.SyntheticInput = "color=blue:size=322x182:rate=15,crop=321:181:0:0:exact=1";
                await editing.ResumeAsync(); await Task.Delay(500);
                var clip = await editing.FinishAsync();
                Check(Math.Abs(clip.Seconds - 6) < .01 && editing.SegmentCount == 2, "resumed timeline");
                double previewDuration = await tools.DurationAsync(clip.PreviewPath);
                Check(Math.Abs(previewDuration - 6) < .1, "static padding/segment timing preserved");
                string gif = Path.Combine(Output, "trimmed.gif"), mp4 = Path.Combine(Output, "trimmed.mp4");
                var gifOptions = new RecordingExportOptions(RecordingFormat.Gif, 10, .5, .3, 5.7);
                await RecordingExporter.ExportAsync(tools, clip, gifOptions, gif, null, default);
                var decoder = BitmapDecoder.Create(new Uri(gif), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Check(decoder.Frames.Count > 5 && decoder.Frames[0].PixelWidth == 160 && decoder.Frames[0].PixelHeight == 90, "GIF dimensions and animation");
                Check(Math.Abs(await tools.DurationAsync(gif) - 5.4) < .12, "GIF trimmed duration");
                Check(Encoding.ASCII.GetString(await File.ReadAllBytesAsync(gif)).Contains("NETSCAPE2.0"), "GIF loop extension");
                await RecordingExporter.ExportAsync(tools, clip, gifOptions with { Format = RecordingFormat.Mp4, Fps = 15, Scale = 1 }, mp4, null, default);
                using var metadata = await ProbeAsync(tools, mp4);
                var stream = metadata.RootElement.GetProperty("streams")[0];
                Check(metadata.RootElement.GetProperty("streams").GetArrayLength() == 1 && stream.GetProperty("codec_name").GetString() == "h264" &&
                    stream.GetProperty("pix_fmt").GetString() == "yuv420p" && stream.GetProperty("width").GetInt32() == 322 && stream.GetProperty("height").GetInt32() == 182, "MP4 compatibility, no audio, even padding");
                Check(Math.Abs(await tools.DurationAsync(mp4) - 5.4) < .12, "MP4 trimmed duration");
                // Check the timeline across a paused/static section by decoding actual pixels.
                foreach (var sample in new[] { (Time: 1.5, Name: "before", Red: true), (Time: 4.5, Name: "after", Red: false) })
                {
                    string image = Path.Combine(Output, sample.Name + ".png");
                    await tools.RunAsync(new[] { "-ss", MediaTools.Number(sample.Time), "-i", clip.PreviewPath, "-frames:v", "1", "-update", "1", image });
                    var bitmap = BitmapFrame.Create(new Uri(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0); byte[] pixel = new byte[4];
                    rgb.CopyPixels(new Int32Rect(20, 20, 1, 1), pixel, 4, 0);
                    Check(sample.Red ? pixel[2] > 200 && pixel[0] < 30 : pixel[0] > 200 && pixel[2] < 30, "pause transition pixel " + sample.Name);
                }
                var cancel = new CancellationTokenSource(); cancel.Cancel();
                string protectedFile = Path.Combine(Output, "existing.mp4"); await File.WriteAllTextAsync(protectedFile, "keep existing file");
                bool cancelled = false;
                try { await RecordingExporter.ExportAsync(tools, clip, gifOptions with { Format = RecordingFormat.Mp4 }, protectedFile, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && await File.ReadAllTextAsync(protectedFile) == "keep existing file", "cancel preserves destination");
                Check(!Directory.EnumerateFiles(Output, ".Cut-export-*").Any(), "partial export cleanup");
                bool invalidExport = false;
                try { (gifOptions with { Fps = 60 }).Validate(clip); } catch (ArgumentException) { invalidExport = true; }
                Check(invalidExport, "no invented source frames");
            }
            finally { editing.Dispose(); }
            await WaitDeletedAsync(editingCache);
            log.AppendLine("PASS pause/resume with red/blue pixel verification, static duration, two-format exports, trim, GIF looping/scaling, H.264 yuv420p/no audio/even padding, cancellation preserves existing file and removes partial output.");

            var capped = new RecordingSession(tools, Options(5)) { SyntheticInput = "testsrc=size=321x181:rate=5", ActiveElapsedForTest = () => 72 };
            string capCache = capped.Cache.DirectoryPath;
            try
            {
                await capped.ResumeAsync(); await Task.Delay(500); Check(capped.Seconds == 60, "active time cap");
                var clip = await capped.FinishAsync(); Check(clip.Seconds == 60, "clip cap");
                Check(Math.Abs(await tools.DurationAsync(clip.PreviewPath) - 60) < .21, "actual 60 second media cap with accelerated test clock");
            }
            finally { capped.Dispose(); }
            await WaitDeletedAsync(capCache);
            var active = new RecordingCache(); string activeCache = active.DirectoryPath;
            RecordingCache.CleanupOrphans(); Check(Directory.Exists(activeCache), "orphan cleanup leaves active lease"); active.Dispose();
            await WaitDeletedAsync(activeCache);
            log.AppendLine("PASS 60 second clamp and actual media duration (accelerated clock), active lease protected from orphan cleanup.");

            await UiAsync(tools, log);
            await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-test.txt"), log.AppendLine("ALL RECORDING TESTS PASSED").ToString()); return 0;
        }
        catch (Exception ex)
        {
            foreach (var window in Application.Current.Windows.OfType<RecordingPreviewWindow>().ToArray()) window.Abort();
            foreach (var window in Application.Current.Windows.OfType<RecordingWindow>().ToArray()) window.Abort();
            await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-test.txt"), log.AppendLine(ex.ToString()).ToString()); return 1;
        }
    }
    private static async Task UiAsync(MediaTools tools, StringBuilder log)
    {
        byte[] pixels = Enumerable.Repeat((byte)230, 800 * 600 * 4).ToArray();
        var image = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgr32, null, pixels, 800 * 4); image.Freeze();
        var capture = new CaptureSession(new DesktopSnapshot(new[] { new MonitorSnapshot(new(0, 0, 800, 600), image) }));
        Exception? failure = null; bool previewed = false;
        using var occupied = new HotkeyService(); bool blockedF8 = occupied.TryRegister(0, 0x77);
        capture.CreateRecordingWindow = (area, monitor) =>
        {
            var window = new RecordingWindow(area, monitor) { CountdownEnabled = false,
                CreateSession = (media, options) => new(media, options) { SyntheticInput = $"testsrc=size={options.Area.Width}x{options.Area.Height}:rate={options.Fps}" } };
            window.CreatePreview = (media, clip) =>
            {
                var preview = new RecordingPreviewWindow(media, clip);
                preview.Loaded += async (_, _) =>
                {
                    try
                    {
                        previewed = true; await Task.Delay(800);
                        Render(preview, "preview.png"); preview.Width = 700; preview.Height = 560; await Task.Delay(100); Render(preview, "preview-compact.png");
                        preview.SetTrimForTest(.1, clip.Seconds); preview.SetFormatForTest(RecordingFormat.Mp4);
                        Check(preview.ExportOptions.Fps == clip.Options.Fps, "MP4 source fps default");
                        preview.SetFormatForTest(RecordingFormat.Gif); Check(preview.ExportOptions.Fps == 15, "GIF 15 fps default");
                        preview.ChoosePathForTest = () => null; await preview.ExportAsync(); Check(preview.IsVisible, "save dialog cancel keeps preview");
                        preview.ChoosePathForTest = () => Path.Combine(Output, "ui.gif"); await preview.ExportAsync(); Check(File.Exists(Path.Combine(Output, "ui.gif")), "real preview export action");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { preview.Abort(); }
                };
                return preview;
            };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    Render(window, "recording-ready.png");
                    window.CountdownEnabled = true;
                    var countdownAttempt = window.ToggleAsync();
                    for (int i = 0; i < 60 && window.SessionForTest is null; i++) await Task.Delay(30);
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await countdownAttempt;
                    Check(window.SessionForTest is null && Descendants<ComboBox>(window).All(c => c.IsEnabled), "cancel countdown releases session and re-enables options");
                    window.CountdownEnabled = false; await window.ToggleAsync();
                    Check(window.SessionForTest?.State == RecordingState.Recording, "UI start action: " + window.MessageForTest);
                    if (blockedF8) Check(window.StopShortcutForTest == "Ctrl + Alt + F8", "occupied F8 falls back explicitly");
                    await Task.Delay(550); Render(window, "recording-active.png"); await window.ToggleAsync();
                    Check(window.SessionForTest?.State == RecordingState.Paused, "UI pause action");
                    await window.ToggleAsync(); await Task.Delay(450); await window.FinishAsync();
                }
                catch (Exception ex) { failure = ex; window.Abort(); }
            };
            return window;
        };
        capture.Show(); capture.Selection.SetSelection(new(20, 20, 321, 181)); capture.EndDrag(new(100, 100));
        var overlay = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
        var record = Descendants<Button>(overlay).First(b => AutomationProperties.GetName(b) == "录制");
        Check(record.Width == 36 && record.Height == 36, "record SVG button dimensions");
        record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(failure is null, "UI: " + failure); Check(previewed && capture.IsFinished, "whole screenshot -> recording -> preview -> export -> session teardown");
        using var released = new HotkeyService();
        if (blockedF8) Check(released.TryRegister(3, 0x77), "fallback stop shortcut released");
        log.AppendLine("PASS actual toolbar entry, synthetic full session, start/pause/resume/stop, preview normal/compact renders, per-format defaults, save cancel, real export action, global stop shortcut release.");
        await Task.CompletedTask;
    }

    internal static async Task<int> NativeAsync()
    {
        Directory.CreateDirectory(Output);
        var log = new StringBuilder("Native recording: captures only synthetic topmost test window, never stores user desktop.\n");
        Window? target = null, excluded = null;
        try
        {
            var tools = await MediaTools.LoadAsync(); var monitor = NativeMethods.Monitors().First();
            target = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
                Background = new SolidColorBrush(Color.FromRgb(32, 96, 128)), Width = 480, Height = 320,
                Content = new TextBlock { Text = "Cut synthetic recording\nWindows Graphics Capture / GDI", Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(130, 120, 0, 0) } };
            target.Show(); NativeMethods.SetWindowPos(new WindowInteropHelper(target).Handle, new IntPtr(-1), monitor.X + 40, monitor.Y + 50, 480, 320, 0x0050);
            var area = new PixelRect(monitor.X + 50, monitor.Y + 60, 320, 200);
            excluded = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Width = 70, Height = 50, Background = Brushes.Magenta };
            excluded.Show(); var handle = new WindowInteropHelper(excluded).Handle;
            bool affinity = NativeMethods.SetWindowDisplayAffinity(handle, 0x11);
            NativeMethods.SetWindowPos(handle, new IntPtr(-1), area.X + 40, area.Y + 30, 70, 50, 0x0050);
            await Task.Delay(300);
            Check(NativeMethods.IsWindowVisible(new WindowInteropHelper(target).Handle), "synthetic native target must be visible");
            var hit = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = area.X + 10, Y = area.Y + 10 }), 2);
            NativeMethods.GetWindowRect(new WindowInteropHelper(target).Handle, out var actualBounds);
            var hitClass = new StringBuilder(256); NativeMethods.GetClassName(hit, hitClass, hitClass.Capacity);
            if (hitClass.ToString().StartsWith("LockScreen", StringComparison.Ordinal))
            {
                log.AppendLine("BLOCKED: Windows desktop is locked. Stopped before capture because the synthetic test window does not own the selected screen pixels. Unlock the desktop and rerun --recording-native-test.");
                await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-native-test.txt"), log.ToString()); return 2;
            }
            Check(hit == new WindowInteropHelper(target).Handle, $"refuse native capture unless synthetic target owns selected pixels: target={new WindowInteropHelper(target).Handle}, hit={hit}, class={hitClass}, bounds={actualBounds.Left},{actualBounds.Top},{actualBounds.Right},{actualBounds.Bottom}, area={area}");
            foreach (var mode in new[] { (Gdi: false, Fps: 5), (Gdi: false, Fps: 60), (Gdi: true, Fps: 10) })
            {
                var session = new RecordingSession(tools, new(area, monitor, mode.Fps, false)) { ForceGdi = mode.Gdi };
                string directory = session.Cache.DirectoryPath;
                try
                {
                    await session.ResumeAsync(); await Task.Delay(1300);
                    double actualFps = session.Progress.Fps; string backend = session.Backend;
                    var clip = await session.FinishAsync();
                    string frame = Path.Combine(Output, $"native-{mode.Gdi}-{mode.Fps}.png");
                    await tools.RunAsync(new[] { "-i", clip.PreviewPath, "-frames:v", "1", "-update", "1", frame });
                    var bitmap = BitmapFrame.Create(new Uri(frame), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Check(bitmap.PixelWidth == 320 && bitmap.PixelHeight == 200, "native physical dimensions");
                    if (affinity)
                    {
                        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0); byte[] pixel = new byte[4];
                        converted.CopyPixels(new Int32Rect(60, 40, 1, 1), pixel, 4, 0);
                        Check(Math.Abs(pixel[0] - 128) < 15 && Math.Abs(pixel[1] - 96) < 15 && Math.Abs(pixel[2] - 32) < 15, "control exclusion native pixel");
                    }
                    Check(clip.Seconds < 5 && clip.Seconds > .5, "static native timing and stop response");
                    log.AppendLine($"PASS requested {mode.Fps} FPS, backend {backend}, observed update FPS {actualFps:0.##}, output {clip.Seconds:0.###}s, physical region/exclusion/static stop.");
                }
                finally { session.Dispose(); }
                await WaitDeletedAsync(directory);
            }
            await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-native-test.txt"), log.AppendLine("ALL NATIVE RECORDING TESTS PASSED").ToString()); return 0;
        }
        catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-native-test.txt"), log.AppendLine(ex.ToString()).ToString()); return 1; }
        finally { excluded?.Close(); target?.Close(); }
    }
}
