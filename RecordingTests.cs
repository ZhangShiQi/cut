using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Controls;
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
    private static Window CheckSelectionFrame(RecordingWindow window, PixelRect area, string stage)
    {
        var frame = window.SelectionFrameForTest;
        Check(frame is { IsVisible: true }, stage + ": recording region remains visible");
        var handle = new WindowInteropHelper(frame!).Handle;
        Check(NativeMethods.GetWindowRect(handle, out var bounds) && bounds.Left == area.X - 4 && bounds.Top == area.Y - 4 &&
            bounds.Right == area.Right + 4 && bounds.Bottom == area.Bottom + 4, stage + ": frame surrounds the exact physical capture region");
        Check((NativeMethods.GetWindowLong(handle, -20) & 0x08000020) == 0x08000020, stage + ": frame allows mouse input through without activation");
        return frame!;
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
            CheckTrimRanges(log);
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
                foreach (var singleFormat in new[] { RecordingFormat.Gif, RecordingFormat.Mp4 })
                {
                    string single = Path.Combine(Output, singleFormat == RecordingFormat.Gif ? "single-frame.gif" : "single-frame.mp4");
                    await RecordingExporter.ExportAsync(tools, clip, new(singleFormat, 5, .5, 3, 3 + 1d / clip.Options.Fps), single, null, default);
                    Check(await tools.DurationAsync(single) > 0, "one source frame survives lower output frame rate: " + singleFormat);
                    if (singleFormat == RecordingFormat.Gif)
                    {
                        var singleDecoder = BitmapDecoder.Create(new Uri(single), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        Check(singleDecoder.Frames.Count == 1, "one-frame GIF is readable");
                    }
                    else
                    {
                        using var singleMetadata = await ProbeAsync(tools, single);
                        Check(singleMetadata.RootElement.GetProperty("streams")[0].GetProperty("nb_frames").GetString() == "1", "one-frame MP4 is readable");
                    }
                }
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
    private enum StopMode { Managed, Button, Hotkey, Limit }
    private static async Task UiAsync(MediaTools tools, StringBuilder log, StopMode mode = StopMode.Managed)
    {
        byte[] pixels = Enumerable.Repeat((byte)230, 800 * 600 * 4).ToArray();
        var image = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgr32, null, pixels, 800 * 4); image.Freeze();
        var capture = new CaptureSession(new DesktopSnapshot(new[] { new MonitorSnapshot(new(0, 0, 800, 600), image) }));
        Exception? failure = null; bool previewed = false;
        using var occupied = new HotkeyService(); bool blockedF8 = occupied.TryRegister(0, 0x77);
        capture.CreateRecordingWindow = (area, monitor) =>
        {
            Window? competing = null; MediaProcess? external = null; bool competingActivated = false;
            IntPtr externalHandle = IntPtr.Zero;
            var window = new RecordingWindow(area, monitor) { CountdownEnabled = false,
                CreateSession = (media, options) => new(media, options) { SyntheticInput = $"testsrc=size={options.Area.Width}x{options.Area.Height}:rate={options.Fps}" } };
            window.CreatePreview = (media, clip) =>
            {
                Check(window.SelectionFrameForTest is null, "selection frame removed before preview");
                Check(window.WindowState == WindowState.Normal && !NativeMethods.IsIconic(new WindowInteropHelper(window).Handle), "preview owner is not minimized");
                var preview = new RecordingPreviewWindow(media, clip);
                preview.Loaded += async (_, _) =>
                {
                    try
                    {
                        previewed = true; await Task.Delay(800);
                        var previewHandle = new WindowInteropHelper(preview).Handle;
                        Check(preview.IsVisible && preview.WindowState == WindowState.Normal && NativeMethods.IsWindowVisible(previewHandle), "preview is shown immediately after stopping");
                        Check(!preview.Topmost, "preview returns to normal stacking after initial presentation");
                        if (mode != StopMode.Managed)
                        {
                            Check(IsAbove(previewHandle, externalHandle), mode + ": preview is above the independent foreground process");
                            if (mode is StopMode.Button or StopMode.Hotkey)
                                Check(preview.IsActive && NativeMethods.GetForegroundWindow() == previewHandle, mode + ": user-triggered stop activates preview");
                            log.AppendLine($"PASS {mode}: preview visible above independent process, active={preview.IsActive}, normal stacking restored.");
                        }
                        else if (competingActivated)
                            Check(preview.IsActive && NativeMethods.GetForegroundWindow() == previewHandle, "preview takes foreground focus from the previously active window");
                        if (mode == StopMode.Managed)
                            log.AppendLine(competingActivated ? "PASS preview activated in foreground over a competing window; normal stacking restored." : "NOTE foreground activation test unavailable on this desktop; visible preview and non-minimized owner verified.");
                        if (mode is StopMode.Managed or StopMode.Button) await CheckTimelinePlaybackAsync(preview, clip, log, mode == StopMode.Button);
                        Render(preview, "preview.png"); preview.Width = 700; preview.Height = 560; await Task.Delay(100); Render(preview, "preview-compact.png");
                        preview.SetTrimForTest(.1, clip.Seconds); preview.SetFormatForTest(RecordingFormat.Mp4);
                        Check(preview.ExportOptions.Fps == clip.Options.Fps, "MP4 source fps default");
                        preview.SetFormatForTest(RecordingFormat.Gif); Check(preview.ExportOptions.Fps == 15, "GIF 15 fps default");
                        preview.ChoosePathForTest = () => null; await preview.ExportAsync(); Check(preview.IsVisible, "save dialog cancel keeps preview");
                        preview.ChoosePathForTest = () => Path.Combine(Output, "ui.gif"); await preview.ExportAsync(); Check(File.Exists(Path.Combine(Output, "ui.gif")), "real preview export action");
                        Descendants<Button>(preview).Single(b => Equals(b.Content, "重新录制")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(preview.RestartRequested, "actual restart action returns to recording settings");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { if (preview.IsVisible) preview.Abort(); }
                };
                return preview;
            };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var initialFrame = CheckSelectionFrame(window, area, "recording settings opened before start");
                    Render(initialFrame, "recording-region-ready.png");
                    Render(window, "recording-ready.png");
                    window.CountdownEnabled = true;
                    var countdownAttempt = window.ToggleAsync();
                    for (int i = 0; i < 60 && window.SessionForTest is null; i++) await Task.Delay(30);
                    CheckSelectionFrame(window, area, "countdown");
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    await countdownAttempt;
                    Check(window.SessionForTest is null && Descendants<ComboBox>(window).All(c => c.IsEnabled), "cancel countdown releases session and re-enables options");
                    Check(ReferenceEquals(initialFrame, CheckSelectionFrame(window, area, "countdown cancelled")), "cancel countdown preserves the same region frame");
                    window.CountdownEnabled = false; await window.ToggleAsync();
                    Check(window.SessionForTest?.State == RecordingState.Recording, "UI start action: " + window.MessageForTest);
                    if (blockedF8) Check(window.StopShortcutForTest == "Ctrl + Alt + F8", "occupied F8 falls back explicitly");
                    await Task.Delay(550); Render(window, "recording-active.png"); await window.ToggleAsync();
                    Check(window.SessionForTest?.State == RecordingState.Paused, "UI pause action");
                    CheckSelectionFrame(window, area, "paused");
                    await window.ToggleAsync(); await Task.Delay(450);
                    if (mode == StopMode.Managed)
                    {
                        competing = new Window { Title = "Cut · 合成前台窗口", Width = 500, Height = 300, ShowInTaskbar = false,
                            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new TextBlock { Text = "停止前的另一个窗口", Margin = new Thickness(20) } };
                        competing.Show(); competingActivated = competing.Activate();
                        await window.FinishAsync();
                    }
                    else
                    {
                        external = new MediaProcess(Path.Combine(AppContext.BaseDirectory, "Cut.exe"), new[] { "--recording-focus-fixture" });
                        Check(await Task.WhenAny(external.FirstFrame, Task.Delay(10000)) == external.FirstFrame, "independent fixture ready: " + external.Error);
                        string handle = external.Output.Split('\n').Single(l => l.StartsWith("fixture_hwnd=", StringComparison.Ordinal))[13..].Trim();
                        externalHandle = new IntPtr(long.Parse(handle, System.Globalization.CultureInfo.InvariantCulture));
                        NativeMethods.GetWindowThreadProcessId(externalHandle, out uint fixtureProcess);
                        Check(fixtureProcess != Environment.ProcessId && fixtureProcess != 0, "fixture runs in a separate process");
                        // Make the fixture available for the setup click even if the
                        // desktop currently has another full-screen/foreground window.
                        Check(NativeMethods.SetWindowPos(externalHandle, new IntPtr(-1), 0, 0, 0, 0, 0x0053),
                            "raise owned fixture for setup; exited=" + external.HasExited);
                        // The recording controller is topmost and may cover the center
                        // of a fixture on a small monitor. Use a verified visible point.
                        ClickWindowInterior(externalHandle);
                        await Task.Delay(150);
                        Check(NativeMethods.GetForegroundWindow() == externalHandle, "independent fixture owns foreground before stop");
                        Check(NativeMethods.SetWindowPos(externalHandle, new IntPtr(-2), 0, 0, 0, 0, 0x0013), "fixture returns to normal stacking before stop");
                        if (mode == StopMode.Button)
                        {
                            var button = Descendants<Button>(window).Single(b => Equals(b.Content, "停止并预览"));
                            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                            ClickWindow(new WindowInteropHelper(window).Handle, new((int)point.X, (int)point.Y));
                        }
                        else if (mode == StopMode.Hotkey) PressStopKey(window.StopShortcutForTest != "F8");
                        else window.SessionForTest!.ActiveElapsedForTest = () => 60;
                        long deadline = Environment.TickCount64 + 45000;
                        while (window.IsVisible && window.SessionForTest is not null && failure is null && Environment.TickCount64 < deadline) await Task.Delay(50);
                        Check(failure is null && previewed && window.IsVisible && window.SessionForTest is null, mode + ": preview and restart completed: " + failure + "; " + window.MessageForTest);
                    }
                    Check(!initialFrame.IsVisible, "first region frame closed on preview");
                    var restartedFrame = CheckSelectionFrame(window, area, "restart before recording begins");
                    window.Abort(); Check(!restartedFrame.IsVisible, "cancel closes the region frame");
                }
                catch (Exception ex) { failure = ex; window.Abort(); }
                finally { competing?.Close(); external?.Dispose(); }
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
        log.AppendLine("PASS actual toolbar entry, region frame visible in settings/countdown/cancelled countdown/pause/restart with exact physical bounds and mouse passthrough, frame removed on preview/close; full session, per-format defaults, export, restart and shortcut release.");
        await Task.CompletedTask;
    }

    private static void CheckTrimRanges(StringBuilder log)
    {
        var random = new Random(7841);
        foreach (int fps in RecordingOptions.FrameRates)
        foreach (double duration in new[] { 1d / fps, 1d / fps + .001, 3.134, 59.987, 60 })
        {
            var range = new RecordingTrimRange(duration, fps);
            void Valid()
            {
                Check(range.Start >= 0 && range.End <= duration && range.Start < range.End && range.Length >= range.MinimumLength - 1e-9, "trim interval invariant");
                Check(range.ConstrainPosition(-999) >= range.Start && range.ConstrainPosition(999) <= range.LastPreviewPosition, "preview position stays within valid frames");
            }
            for (int i = 0; i < 600; i++)
            {
                double value = (random.NextDouble() * 3 - 1) * duration;
                if (i % 3 == 0) range.SetStart(value);
                else if (i % 3 == 1) range.SetEnd(value);
                else range.SetRange(value, (random.NextDouble() * 3 - 1) * duration);
                Valid();
            }
            range.Reset(); range.SetStart(duration + 10); Valid();
            Check(Math.Abs(range.Length - range.MinimumLength) < 1e-8, "crossed start retains one frame");
            range.Reset(); range.SetEnd(-10); Valid();
            Check(Math.Abs(range.Length - range.MinimumLength) < 1e-8, "crossed end retains one frame");
            range.Reset(); Check(range.Start == 0 && range.End == duration, "restore complete duration including partial final frame");
        }
        log.AppendLine("PASS 15,000 trim edits across five frame rates, single/partial frames and 60s clips: no crossing, no empty intervals, valid preview positions and full-range reset.");
    }
    private static async Task WaitForAsync(Func<bool> condition, string label, int milliseconds = 4000)
    {
        long deadline = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(20);
        Check(condition(), label);
    }
    private static async Task CheckTimelinePlaybackAsync(RecordingPreviewWindow preview, RecordingClip clip, StringBuilder log, bool native)
    {
        var timeline = preview.TimelineForTest;
        await WaitForAsync(() => preview.MediaReadyForTest && timeline.HasFilmstrip, "media decoder and real thumbnail strip loaded");
        Thumb Handle(string label) => Descendants<Thumb>(timeline).Single(t => AutomationProperties.GetName(t) == label);
        void Drag(Thumb thumb, double delta, bool cancel = false)
        {
            thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
            thumb.RaiseEvent(new DragDeltaEventArgs(delta, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            thumb.RaiseEvent(new DragCompletedEventArgs(delta, 0, cancel) { RoutedEvent = Thumb.DragCompletedEvent });
            preview.ExportOptions.Validate(clip);
        }
        timeline.SetSelection(.2, clip.Seconds * .7);
        double end = timeline.Range.End;
        if (native)
        {
            preview.UpdateLayout();
            var thumb = Handle("裁剪起点");
            var origin = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            var target = timeline.PointToScreen(new Point(timeline.ActualWidth - 22, 52));
            var handle = new WindowInteropHelper(preview).Handle;
            Check(NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = (int)origin.X, Y = (int)origin.Y }), 2) == handle, "native trim drag targets only own preview");
            SetCursorPos((int)origin.X, (int)origin.Y);
            await Task.Delay(60);
            try
            {
                Check(SendInput(1, new[] { new TestInput { MouseFlags = 2 } }, 40) == 1, "native trim mouse down");
                await Task.Delay(60);
                Check(thumb.IsDragging, "native mouse captures trim handle; hit=" + preview.InputHitTest(preview.PointFromScreen(origin))?.GetType().Name);
                for (int i = 1; i <= 10; i++) { SetCursorPos((int)(origin.X + (target.X - origin.X) * i / 10), (int)(origin.Y + (target.Y - origin.Y) * i / 10)); await Task.Delay(15); }
            }
            finally { SendInput(1, new[] { new TestInput { MouseFlags = 4 } }, 40); }
            await Task.Delay(80);
        }
        else Drag(Handle("裁剪起点"), timeline.ActualWidth * 3);
        Check(Math.Abs(timeline.Range.End - end) < 1e-8 && Math.Abs(timeline.Range.Length - timeline.Range.MinimumLength) < 1e-8,
            $"start cannot cross the end: {timeline.Range.Start}–{timeline.Range.End}, expected end={end}, native={native}");
        preview.ExportOptions.Validate(clip);
        timeline.SetSelection(clip.Seconds * .3, clip.Seconds - .1);
        double begin = timeline.Range.Start;
        Drag(Handle("裁剪终点"), -timeline.ActualWidth * 3);
        Check(timeline.Range.Start == begin && Math.Abs(timeline.Range.Length - timeline.Range.MinimumLength) < 1e-8, "end cannot cross the start");
        double savedStart = timeline.Range.Start, savedEnd = timeline.Range.End;
        Drag(Handle("裁剪起点"), -timeline.ActualWidth, true);
        Check(timeline.Range.Start == savedStart && timeline.Range.End == savedEnd, "cancel drag restores both endpoints exactly");
        timeline.ResetSelection(); preview.PlayForTest();
        await WaitForAsync(() => preview.PlayerPositionForTest < .3, "reset seeks decoder to the beginning");
        double initial = preview.PlayerPositionForTest;
        await WaitForAsync(() => preview.PlayerPositionForTest > initial + .15 && preview.MediaReadyForTest, "actual video playback advances after crossing and reset");
        byte[] Frame(string name)
        {
            var media = Descendants<MediaElement>(preview).Single();
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(media.ActualWidth)), Math.Max(1, (int)Math.Ceiling(media.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(media); ExportService.SavePng(bitmap, Path.Combine(Output, name));
            byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
        }
        double before = preview.PlayerPositionForTest;
        var firstFrame = Frame("timeline-playback-before.png");
        await WaitForAsync(() => preview.PlayerPositionForTest > before + .25, "video keeps playing after seek settles");
        var secondFrame = Frame("timeline-playback-after.png");
        Check(!firstFrame.SequenceEqual(secondFrame), "rendered video pixels change after restoring playback");
        timeline.ResetSelection();
        var startHandle = Handle("裁剪起点");
        startHandle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(preview), Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Check(Math.Abs(timeline.Range.Start - 1d / clip.Options.Fps) < 1e-8, "keyboard nudges trim start by exactly one frame");
        timeline.ResetSelection();
        log.AppendLine(native ? "PASS real mouse trim drag beyond end: constrained to one frame, reverse drag/cancel/reset valid, rendered video pixels resume changing and keyboard frame step works." :
            "PASS dedicated timeline: crossed endpoints, single frame, cancellation, reset, real thumbnail strip, advancing playback clock AND changing video pixels, keyboard frame step.");
    }

    // Separate test process with no normal app initialization or personal settings.
    internal static async Task<int> FocusFixtureAsync()
    {
        var window = new Window { Title = "Cut · 独立进程前台测试", Width = 500, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = "合成测试窗口\n用于验证录制预览的前台显示", FontSize = 20, Margin = new Thickness(25) } };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ContentRendered += (_, _) => ready.TrySetResult();
        window.Show(); await ready.Task;
        Console.WriteLine("fixture_hwnd=" + new WindowInteropHelper(window).Handle.ToInt64());
        Console.WriteLine("frame=1"); Console.WriteLine("progress=continue"); await Console.Out.FlushAsync();
        // Console's synchronized reader can block inside ReadLineAsync. Never
        // let the fixture's dispatcher wait synchronously for its parent pipe.
        await Task.WhenAny(Task.Run(() => Console.In.ReadLine()), Task.Delay(90000));
        window.Close(); return 0;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct TestInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Key;
        [FieldOffset(12)] public uint KeyFlags;
        [FieldOffset(20)] public uint MouseFlags;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, TestInput[] input, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private static void ClickWindowInterior(IntPtr handle)
    {
        Check(NativeMethods.GetWindowRect(handle, out var bounds), "independent fixture bounds");
        foreach (double y in new[] { .8, .5, .3 })
        foreach (double x in new[] { .8, .5, .2 })
        {
            var point = new PixelPoint(bounds.Left + (int)((bounds.Right - bounds.Left) * x), bounds.Top + (int)((bounds.Bottom - bounds.Top) * y));
            if (NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = point.X, Y = point.Y }), 2) != handle) continue;
            ClickWindow(handle, point); return;
        }
        var foregroundClass = new StringBuilder(256);
        NativeMethods.GetClassName(NativeMethods.GetForegroundWindow(), foregroundClass, foregroundClass.Capacity);
        throw new InvalidOperationException("Independent fixture has no visible click point; desktop may be locked or covered. Foreground class=" + foregroundClass);
    }
    private static void ClickWindow(IntPtr handle, PixelPoint point)
    {
        Check(NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = point.X, Y = point.Y }), 2) == handle,
            "refuse click unless owned test window is under pointer");
        Check(SetCursorPos(point.X, point.Y), "move test pointer");
        var input = new[] { new TestInput { MouseFlags = 2 }, new TestInput { MouseFlags = 4 } };
        Check(SendInput(2, input, 40) == 2, "actual test click");
    }
    private static void PressStopKey(bool modifiers)
    {
        Check(!ScrollInputService.HasHeldInput(), "stop-key test requires no held input");
        var keys = modifiers ? new ushort[] { 0x11, 0x12, 0x77 } : new ushort[] { 0x77 };
        var releases = keys.Reverse().Select(k => new TestInput { Type = 1, Key = k, KeyFlags = 2 }).ToArray();
        try
        {
            var input = keys.Select(k => new TestInput { Type = 1, Key = k }).Concat(releases).ToArray();
            Check(SendInput((uint)input.Length, input, 40) == input.Length, "actual global stop shortcut");
        }
        finally { SendInput((uint)releases.Length, releases, 40); }
    }
    private static bool IsAbove(IntPtr upper, IntPtr lower)
    {
        int order = 0, upperOrder = -1, lowerOrder = -1;
        NativeMethods.EnumWindows((IntPtr handle, IntPtr _) =>
        {
            if (handle == upper) upperOrder = order;
            if (handle == lower) lowerOrder = order;
            order++; return true;
        }, IntPtr.Zero);
        return upperOrder >= 0 && lowerOrder >= 0 && upperOrder < lowerOrder;
    }
    internal static async Task<int> UiNativeAsync()
    {
        Directory.CreateDirectory(Output);
        var log = new StringBuilder("Native recording UI: independent synthetic foreground process; real stop-button click, global shortcut, accelerated 60s stop. Only generated media recorded.\n");
        var pointer = NativeMethods.CursorPosition();
        try
        {
            var tools = await MediaTools.LoadAsync();
            foreach (var mode in new[] { StopMode.Button, StopMode.Hotkey, StopMode.Limit }) await UiAsync(tools, log, mode);
            log.AppendLine("ALL NATIVE RECORDING UI CHECKS PASSED");
            await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-ui-native-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            foreach (var window in Application.Current.Windows.OfType<RecordingPreviewWindow>().ToArray()) window.Abort();
            foreach (var window in Application.Current.Windows.OfType<RecordingWindow>().ToArray()) window.Abort();
            await File.WriteAllTextAsync(Path.Combine("artifacts", "recording-ui-native-test.txt"), log.AppendLine(ex.ToString()).ToString()); return 1;
        }
        finally { SetCursorPos(pointer.X, pointer.Y); }
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
