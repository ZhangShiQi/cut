using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class ScrollTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint current, uint foreground, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    internal static void FocusFixture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        uint current = GetCurrentThreadId(), foreground = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = foreground != current && AttachThreadInput(current, foreground, true);
        try { window.Activate(); SetForegroundWindow(handle); }
        finally { if (attached) AttachThreadInput(current, foreground, false); }
    }
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    internal static BitmapSource Fixture(int width = 640, int height = 2600, bool chat = false)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var random = new Random(321);
            for (int y = 0, row = 1; y < height; y += 46, row++)
            {
                double x = chat ? 12 : 18, extent = chat ? 180 + random.Next(90) : width - 36;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)random.Next(180, 245), (byte)random.Next(180, 245), (byte)random.Next(180, 245))), null, new Rect(x, y, extent, 40));
                var value = new FormattedText($"{row:D3}  {(chat ? "聊天消息" : "网页正文")} {random.Next(10000, 99999)}", CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 18, Brushes.Black, 1);
                dc.DrawText(value, new Point(x + 6, y + 5));
            }
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    private static ScrollFrame Frame(BitmapSource image, int y, int height = 500) => ScrollFrame.From(new CroppedBitmap(image, new Int32Rect(0, y, image.PixelWidth, height)));
    internal static void Same(BitmapSource actual, BitmapSource expected, string label)
    {
        var a = ScrollFrame.From(actual); var b = ScrollFrame.From(expected);
        Check(a.Width == b.Width && a.Height == b.Height, label + " dimensions");
        for (int p = 0; p < a.Pixels.Length; p += 4)
            if (a.Pixels[p] != b.Pixels[p] || a.Pixels[p + 1] != b.Pixels[p + 1] || a.Pixels[p + 2] != b.Pixels[p + 2]) throw new InvalidOperationException(label + " pixel " + p / 4);
    }
    internal static void Preview(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); ExportService.SavePng(image, path);
    }
    private static async Task Feed(ScrollCaptureWindow window, ScrollFrame frame)
    { await window.AcceptFrameAsync(frame); await window.AcceptFrameAsync(frame); await window.AcceptFrameAsync(frame); }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Cut manual scrolling tests: synthetic pages/chat frames, no network or clipboard writes.\n");
        try
        {
            ScrollFeatureTests.Run();
            log.AppendLine("PASS generated translucent foreground + fixed wallpaper, exact integer shifts both ways, reverse/no-overlap and localized animation rejection");
            foreach (bool chat in new[] { false, true })
            {
                var image = Fixture(chat: chat);
                var down = new ScrollAssembler(Frame(image, 0), ScrollDirection.Down);
                int offset = 0;
                foreach (int d in new[] { 80, 150, 235, 37, 260, 100 })
                {
                    offset += d; var next = Frame(image, offset);
                    var match = ScrollMatcher.Match(down.Last, next, ScrollDirection.Down);
                    Check(match.Kind == ScrollMatchKind.Matched && match.Distance == d, $"Down {chat} shift {d}, got {match}");
                    Check(down.Append(next, d), "Append");
                    Same(down.Build(), new CroppedBitmap(image, new Int32Rect(0, 0, image.PixelWidth, offset + 500)), "Down no missing or repeated rows");
                }
                Check(ScrollMatcher.Match(down.Last, down.Last, ScrollDirection.Down).Kind == ScrollMatchKind.Same, "No duplicate append");
                Check(ScrollMatcher.Match(down.Last, Frame(image, offset - 80), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Wrong direction rejected");
                Check(ScrollMatcher.Match(down.Last, Frame(image, offset + 600), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "No overlap rejected");
                down.Undo(); offset -= 100;
                Same(down.Last.Image(), Frame(image, offset).Image(), "Undo restores matching baseline");
                var nextAgain = Frame(image, offset + 50); var again = ScrollMatcher.Match(down.Last, nextAgain, ScrollDirection.Down);
                Check(again.Distance == 50 && down.Append(nextAgain, 50), "Continue after undo");

                offset = 1800; var up = new ScrollAssembler(Frame(image, offset), ScrollDirection.Up);
                foreach (int d in new[] { 100, 230, 65, 250, 120 })
                {
                    offset -= d; var next = Frame(image, offset); var match = ScrollMatcher.Match(up.Last, next, ScrollDirection.Up);
                    Check(match.Kind == ScrollMatchKind.Matched && match.Distance == d, $"Up {chat} shift {d}, got {match}");
                    up.Append(next, d);
                    Same(up.Build(), new CroppedBitmap(image, new Int32Rect(0, offset, image.PixelWidth, 2300 - offset)), "Up chronological order, exact seams");
                }
                up.Undo(); offset += 120; Same(up.Last.Image(), Frame(image, offset).Image(), "Up undo restores head baseline");
            }
            log.AppendLine("PASS exact-pixel page/chat stitching in both directions, varied scroll distances, no duplicates/gaps, reverse/no-overlap rejection, undo/continue");
            var staticPage = Fixture(); var originalFrame = Frame(staticPage, 0); var movingFrame = Frame(staticPage, 120);
            var stickyPixels = (byte[])movingFrame.Pixels.Clone();
            Buffer.BlockCopy(originalFrame.Pixels, 0, stickyPixels, 0, movingFrame.Width * 35 * 4);
            var sticky = new ScrollFrame(movingFrame.Width, movingFrame.Height, stickyPixels);
            Check(ScrollMatcher.Match(originalFrame, sticky, ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Sticky header rejected rather than duplicated");
            var trimmed = ScrollMatcher.Match(originalFrame.Rows(35, 465), sticky.Rows(35, 465), ScrollDirection.Down);
            Check(trimmed.Kind == ScrollMatchKind.Matched && trimmed.Distance == 120, "Excluding fixed header restores matching");
            var animatedPixels = (byte[])movingFrame.Pixels.Clone();
            for (int y = 80; y < 240; y++) for (int x = 100; x < 350; x++) animatedPixels[(y * movingFrame.Width + x) * 4] = 0;
            Check(ScrollMatcher.Match(originalFrame, new(movingFrame.Width, movingFrame.Height, animatedPixels), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Large dynamic content rejected");
            log.AppendLine("PASS fixed-header rejection, excluded header recovery and large animated-region rejection");
            var blank = ScrollFrame.From(new WriteableBitmap(300, 400, 96, 96, PixelFormats.Bgr32, null));
            Check(ScrollMatcher.Match(blank, blank, ScrollDirection.Down).Kind == ScrollMatchKind.Same, "Blank not fabricated as scroll");
            byte[] repeatPixels = new byte[300 * 400 * 4];
            for (int y = 0; y < 400; y++) for (int x = 0; x < 300; x++)
            {
                int p = (y * 300 + x) * 4; byte value = (byte)(((x / 6 + y / 10) % 3) * 80);
                repeatPixels[p] = repeatPixels[p + 1] = repeatPixels[p + 2] = value;
            }
            var repeated = new ScrollFrame(300, 400, repeatPixels);
            byte[] shifted = new byte[repeatPixels.Length];
            for (int y = 0; y < 400; y++) Buffer.BlockCopy(repeatPixels, ((y + 10) % 390) * 300 * 4, shifted, y * 300 * 4, 300 * 4);
            Check(ScrollMatcher.Match(repeated, new(300, 400, shifted), ScrollDirection.Down).Kind == ScrollMatchKind.Uncertain, "Ambiguous periodic content rejected");
            var huge = new ScrollAssembler(blank, ScrollDirection.Down);
            while (huge.Height + 200 <= ScrollAssembler.MaxHeight) Check(huge.Append(blank, 200), "Append to height limit");
            Check(!huge.Append(blank, 200) && huge.Height == 30000, "Height cap preserves previous result");
            var tall = huge.Build();
            var tallDoc = new AnnotationDocument(tall.PixelWidth, tall.PixelHeight);
            tallDoc.Tool = MarkTool.Cover; tallDoc.Style = new(Colors.Red); tallDoc.Begin(new(10, 29900), 3); tallDoc.End(new(100, 29980));
            var tallResult = new AnnotationRenderer(tall).Render(tallDoc);
            Check(tallResult.PixelHeight == 30000, "Maximum-height annotation export");
            ExportService.SavePng(tallResult, Path.Combine(output, "scroll-maxheight.png"));
            log.AppendLine("PASS blank/repeated patterns, length cap, 30,000-pixel-high annotated PNG");

            var fixture = Fixture(); var frame0 = Frame(fixture, 0); var frame1 = Frame(fixture, 120);
            var capture = new ScrollCaptureWindow(new(10, 10, 640, 500), new(0, 0, 1600, 1000)) { ValidateForTest = () => true, AutomaticPolling = false, MatchRetryMilliseconds = 0 };
            capture.Show(); await capture.ToggleAsync(); await Feed(capture, frame0);
            Check(capture.Document is not null && capture.Document.Appends == 0, "Stable first frame");
            await capture.AcceptFrameAsync(frame1); Check(capture.Document!.Appends == 0, "Intermediate moving frame not appended");
            await Feed(capture, frame1); Check(capture.Document.Appends == 1, "Stable frame appended");
            capture.Pause(); await Feed(capture, Frame(fixture, 240)); Check(capture.Document.Appends == 1, "Pause ignores new frames");
            await capture.ToggleAsync(); await Feed(capture, Frame(fixture, 240)); Check(capture.Document.Appends == 2, "Resume matches last accepted baseline");
            await capture.UndoAsync(); Check(!capture.Running && capture.Document.Appends == 1, "Undo pauses capture");
            Preview(capture, Path.Combine(output, "scroll-capture-preview.png"));
            await capture.ToggleAsync(); await Feed(capture, Frame(fixture, 900));
            Check(!capture.Running && capture.Document.Appends == 1, "Unreliable seam auto pauses and retains result");
            await capture.CompleteAsync(); Check(capture.Result is not null, "Finish produces image");

            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            var late = new ScrollCaptureWindow(new(0, 0, 640, 500), new(0, 0, 1600, 1000))
            { ValidateForTest = () => true, AutomaticPolling = false, Capture = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); return frame0.Image(); } };
            late.Show(); await late.ToggleAsync(); var pending = late.PollAsync();
            Check(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))), "Capture entered");
            late.Pause(); release.Set(); await pending; Check(late.Document is null, "Late frame discarded after pause"); late.Close();
            log.AppendLine("PASS stable-frame gating, pause/resume, undo pause, unreliable seam pause, late capture invalidation");

            var editor = new LongImageEditor(capture.Result!); editor.Show();
            editor.SetTool(MarkTool.Cover); editor.Document.Begin(new(10, 10), 3); editor.Document.End(new(100, 80));
            editor.SetTool(MarkTool.Text); editor.BeginText(new(180, 100)); editor.TextEditor!.Text = "长图标注 Hello";
            await editor.KeyAsync(Key.Enter, ModifierKeys.Control);
            Check(editor.Document.Items.Count == 2, "Long editor text commit");
            await editor.KeyAsync(Key.Z, ModifierKeys.Control); Check(editor.Document.Items.Count == 1, "Long editor undo");
            await editor.KeyAsync(Key.Y, ModifierKeys.Control); Check(editor.Document.Items.Count == 2, "Long editor redo");
            Preview(editor, Path.Combine(output, "scroll-editor-preview.png"));
            var composed = editor.Output();
            BitmapSource? seenOcr = null, seenVision = null;
            editor.SelectRegion(new(0, 0, 400, 240)); var cropped = editor.Output();
            Check(cropped.PixelWidth == 400 && cropped.PixelHeight == 240, "Local output crop");
            Same(cropped, new CroppedBitmap(composed, new Int32Rect(0, 0, 400, 240)), "Crop of annotated pixels");
            editor.RecognizeText = (image, _) => { seenOcr = image; return Task.FromResult(new OcrResult(Array.Empty<OcrLine>())); };
            await editor.OcrAsync(); Same(cropped, seenOcr!, "Long editor OCR gets composed crop");
            editor.CreateVision = image => { seenVision = image; var dialog = new VisionResultWindow(image); dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)); return dialog; };
            editor.Analyze(); Same(cropped, seenVision!, "Long editor vision gets composed crop");
            editor.ChooseSavePath = () => null; editor.Save(); Check(editor.IsVisible, "Cancel saving preserves editor");
            editor.ChooseSavePath = () => Path.Combine(output, "scroll-editor-output.png"); editor.Save();
            Check(editor.ActionCompleted, "Long editor save completed");
            var copying = new LongImageEditor(capture.Result!); copying.Show(); copying.CopyImage = _ => throw new IOException("Synthetic clipboard contention");
            await copying.CopyAsync(); Check(copying.IsVisible && !copying.ActionCompleted, "Failed copy keeps image");
            BitmapSource? copied = null; copying.CopyImage = image => { copied = image; return Task.CompletedTask; }; await copying.CopyAsync();
            Check(copying.ActionCompleted && copied is not null, "Retry copy completes");
            log.AppendLine("PASS scrollable editor, annotation/text/undo/redo, crop, composed OCR/AI/save/copy, cancellation and retry");

            var session = new CaptureSession(new DesktopSnapshot(new[] { new MonitorSnapshot(new(0, 0, 1000, 720), Fixture(1000, 720)) }));
            session.Show(); session.Selection.SetSelection(new(20, 20, 640, 500)); session.EndDrag(new(30, 30));
            bool visitedCapture = false, visitedEditor = false;
            session.CreateScrollWindow = (area, monitor) =>
            {
                var window = new ScrollCaptureWindow(area, monitor) { ValidateForTest = () => true, AutomaticPolling = false };
                window.Loaded += async (_, _) => { visitedCapture = true; await window.ToggleAsync(); await Feed(window, frame0); await Feed(window, frame1); await window.CompleteAsync(); };
                return window;
            };
            session.CreateLongEditor = image =>
            {
                var window = new LongImageEditor(image);
                window.Loaded += (_, _) => { visitedEditor = true; window.Dispatcher.BeginInvoke(new Action(window.Close)); }; return window;
            };
            var overlay = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            await overlay.HandleKeyAsync(Key.L, ModifierKeys.Control);
            Check(visitedCapture && visitedEditor && session.IsFinished, "Ctrl L -> capture -> editor -> session cleanup");
            log.AppendLine("PASS Ctrl+L integration, live capture transition, editor handoff and overlay cleanup");
            log.AppendLine("ALL SCROLL CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "scroll-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "scroll-test.txt"), log + ex.ToString());
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close(); return 1;
        }
    }
    public static async Task<int> NativeAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Cut native scrolling test: only synthetic application content is captured/saved.\n");
        Window? page = null; ScrollCaptureWindow? capture = null;
        try
        {
            var image = Fixture(700, 2600);
            var scroller = new ScrollViewer { Content = new Image { Source = image, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left }, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            page = new Window { Title = "Cut synthetic scroll fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 700, Height = 500, Left = 120, Top = 100, Content = scroller, Topmost = true };
            page.Show(); page.Activate(); await Task.Delay(180); page.UpdateLayout();
            var origin = scroller.PointToScreen(new Point(0, 0)); var dpi = VisualTreeHelper.GetDpi(scroller);
            var area = new PixelRect((int)origin.X, (int)origin.Y, (int)(scroller.ActualWidth * dpi.DpiScaleX), (int)(scroller.ActualHeight * dpi.DpiScaleY));
            var monitor = NativeMethods.Monitors().First(m => m.Contains(new(area.X, area.Y)));
            var handle = new WindowInteropHelper(page).Handle;
            capture = new(area, monitor) { ResolveTargetForTest = () => handle, AutomaticPolling = false };
            capture.Show(); await capture.ToggleAsync(); FocusFixture(page); await Task.Delay(100);
            for (int i = 0; i < 3; i++) { await capture.PollAsync(); await Task.Delay(50); }
            Check(capture.Document is not null && capture.Running, "Native stable initial frame: " + capture.Message);
            var original = capture.Document!.Last;
            scroller.ScrollToVerticalOffset(120); await Task.Delay(180);
            for (int i = 0; i < 3; i++) { await capture.PollAsync(); await Task.Delay(50); }
            Check(capture.Document.Appends == 1 && capture.Document.Height == area.Height + (int)Math.Round(120 * dpi.DpiScaleY), "Native scroll distance: " + capture.Message);
            // Move the controller over the source: capture exclusion must remove its pixels.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                var before = capture.Document.Last.Image();
                NativeMethods.SetWindowPos(new WindowInteropHelper(capture).Handle, new IntPtr(-1), area.X + 80, area.Y + 30, 390, 430, 0x0010);
                await Task.Delay(150); var after = CaptureService.CaptureRegion(area);
                Same(after, before, "Own toolbar excluded from native capture");
            }
            capture.Pause(); await capture.ToggleAsync(); page.Left += 20; await Task.Delay(100); await capture.PollAsync();
            Check(!capture.Running && capture.Document.Appends == 1, "Moved source pauses without corrupting result");
            ExportService.SavePng(capture.Document.Build(), Path.Combine(output, "scroll-native-output.png"));
            capture.Close(); page.Left -= 20; scroller.ScrollToVerticalOffset(600); await Task.Delay(150);
            capture = new(area, monitor) { ResolveTargetForTest = () => handle, AutomaticPolling = false, Direction = ScrollDirection.Up };
            capture.Show(); await capture.ToggleAsync(); FocusFixture(page); await Task.Delay(100);
            for (int i = 0; i < 3; i++) { await capture.PollAsync(); await Task.Delay(50); }
            Check(capture.Document is not null, "Native up seed: " + capture.Message);
            scroller.ScrollToVerticalOffset(480); await Task.Delay(150);
            for (int i = 0; i < 3; i++) { await capture.PollAsync(); await Task.Delay(50); }
            Check(capture.Document!.Appends == 1 && capture.Document.Height == area.Height + (int)Math.Round(120 * dpi.DpiScaleY), "Native upward scroll: " + capture.Message);
            ExportService.SavePng(capture.Document.Build(), Path.Combine(output, "scroll-native-up-output.png"));
            capture.Close();
            // Exercise the production modal lifecycle as well as the modeless capture tests above.
            var modal = new ScrollCaptureWindow(area, monitor) { ResolveTargetForTest = () => handle, AutomaticPolling = false };
            Exception? modalError = null;
            modal.Loaded += async (_, _) =>
            {
                try
                {
                    await modal.ToggleAsync(); FocusFixture(modal); await Task.Delay(100);
                    for (int i = 0; i < 3; i++) { await modal.PollAsync(); await Task.Delay(50); }
                    Check(modal.Document is not null, "Modal native initial frame: " + modal.Message); await modal.CompleteAsync();
                }
                catch (Exception ex) { modalError = ex; modal.Close(); }
            };
            modal.ShowDialog();
            if (modalError is not null) throw modalError;
            Check(modal.Result is not null, "Native modal session persists until completion");
            page.Close();
            log.AppendLine("PASS native GDI viewport acquisition, real WPF content scroll in both directions, exact displacement, border/toolbar exclusion and target movement guard");
            log.AppendLine("PASS real modal capture lifecycle: start remains in ShowDialog until completed");
            log.AppendLine("Actual browser/WeChat versions and mixed-DPI multi-monitor hardware are not covered by this synthetic source test.");
            log.AppendLine("ALL NATIVE SCROLL CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "scroll-native-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            capture?.Close(); page?.Close(); File.WriteAllText(Path.Combine(output, "scroll-native-test.txt"), log + ex.ToString()); return 1;
        }
    }
}
