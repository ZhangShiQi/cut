using System;
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

internal static class AutoScrollTests
{
    private static void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static BitmapSource Crop(BitmapSource image, int y, int h = 400)
    { var crop = new CroppedBitmap(image, new Int32Rect(0, y, image.PixelWidth, h)); crop.Freeze(); return crop; }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Automatic scroll tests with synthetic frames and injected clock/input (no real wheel input).\n");
        ScrollCaptureWindow? window = null;
        try
        {
            var image = ScrollTests.Fixture(640, 901);
            foreach (var direction in Enum.GetValues<ScrollDirection>())
            {
                var a = ScrollFrame.From(Crop(image, direction == ScrollDirection.Down ? 200 : 201));
                var b = ScrollFrame.From(Crop(image, direction == ScrollDirection.Down ? 201 : 200));
                Check(ScrollMatcher.Match(a, b, direction) == new ScrollMatch(ScrollMatchKind.Matched, 1), "One-pixel final remainder " + direction);
            }
            foreach (var mode in Enum.GetValues<ScrollDirection>())
            foreach (var speed in Enum.GetValues<ScrollSpeed>())
            {
                long clock = 0; int offset = mode == ScrollDirection.Down ? 0 : 501, limit = 501;
                window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
                {
                    AutoScroll = true, Direction = mode, Speed = speed, AutomaticPolling = false,
                    ValidateForTest = () => true, SkipAutoGuardsForTest = true, CountdownMilliseconds = 0, Now = () => clock,
                    Capture = _ => Crop(image, offset),
                    SendWheel = delta => { Check(Math.Sign(delta) == (mode == ScrollDirection.Down ? -1 : 1), "Wheel sign"); offset = Math.Clamp(offset - Math.Sign(delta) * 100, 0, limit); return true; }
                };
                window.Show(); await window.ToggleAsync();
                for (int i = 0; i < 250 && window.Running; i++) { clock += 180; await window.PollAsync(); }
                Check(!window.Running && window.Message.Contains("未检测到新内容"), "Boundary fallback: " + window.Message);
                Check(window.Document?.Height == 901, "Complete 501px extent incl 1px remainder");
                ScrollTests.Same(window.Document!.Build(), image, "Exact stitched output " + mode + "/" + speed);
                int count = window.InputsSent;
                for (int i = 0; i < 8; i++) { clock += 180; await window.PollAsync(); }
                Check(window.InputsSent == count && window.Result is null && window.Document is not null, "Stop preserves preview without late input");
                if (mode == ScrollDirection.Down)
                {
                    image = ScrollTests.Fixture(640, 1101); limit = 701;
                    window.Speed = ScrollSpeed.Fast;
                    await window.ToggleAsync();
                    for (int i = 0; i < 250 && window.Running; i++) { clock += 180; await window.PollAsync(); }
                    Check(window.Document!.Height == 1101, "Continue after boundary and newly loaded content");
                    ScrollTests.Same(window.Document.Build(), image, "Resume output");
                    image = ScrollTests.Fixture(640, 901);
                }
                if (mode == ScrollDirection.Up && speed == ScrollSpeed.Standard)
                    ScrollTests.Preview(window, Path.Combine(output, "auto-scroll-preview.png"));
                await window.CompleteAsync(); Check(window.Result is not null, "Stopped result can be completed");
                log.AppendLine("PASS " + mode + "/" + speed + ": full pixels, 1px tail, boundary stop, preview, continue/complete");
            }
            foreach (var speed in Enum.GetValues<ScrollSpeed>())
            {
                var pace = new AutoScrollPacer(speed); pace.InputSent(0);
                Check(pace.Stable(pace.SettleMilliseconds - 1, 0, 400) == AutoScrollAction.Wait, "Minimum settling wait");
                Check(pace.Stable(pace.SettleMilliseconds, 0, 400) == AutoScrollAction.Scroll, "Step after stable wait");
                pace.InputSent(2000); pace.InputSent(4000);
                Check(pace.Stable(6000, 0, 400) == AutoScrollAction.Stop, "Multiple attempts AND five seconds");
                Check(pace.Stable(6001, 40, 400) == AutoScrollAction.Scroll && pace.AttemptsWithoutProgress == 0, "Lazy content resets end detection");
            }
            long now = 0; int y = 0;
            window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
            {
                AutoScroll = true, AutomaticPolling = false, ValidateForTest = () => true, SkipAutoGuardsForTest = true,
                CountdownMilliseconds = 0, Now = () => now, Capture = _ => Crop(image, y),
                SendWheel = _ => { y += 50; return true; }
            };
            window.Show(); await window.ToggleAsync();
            for (int i = 0; i < 3; i++) { now += 180; await window.PollAsync(); }
            Check(window.InputsSent == 1, "First input only after stable seed");
            window.Pause(); int previousCount = window.InputsSent;
            window.Speed = ScrollSpeed.Fast; await window.ToggleAsync();
            for (int i = 0; i < 3; i++) { now += 180; await window.PollAsync(); }
            Check(window.Document!.Height == 450 && window.InputsSent == previousCount + 1, "Resume captures pending motion before next wheel");
            window.Pause(); window.SendWheel = _ => false; await window.ToggleAsync();
            for (int i = 0; i < 3; i++) { now += 180; await window.PollAsync(); }
            Check(!window.Running && window.Message.Contains("无法向目标窗口发送"), "Input failure is not reported as boundary");
            // Hold a capture in flight and pause before completion: neither append nor input may occur.
            window.SendWheel = _ => true;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var gate = new ManualResetEventSlim(false);
            window.Capture = _ => { entered.TrySetResult(); gate.Wait(); return Crop(image, y); };
            await window.ToggleAsync(); var pending = window.PollAsync(); await entered.Task;
            previousCount = window.InputsSent; int previousHeight = window.Document!.Height;
            window.Pause(); gate.Set(); await pending;
            Check(window.InputsSent == previousCount && window.Document.Height == previousHeight, "No stale capture append/input after pause");
            window.Close();
            log.AppendLine("PASS minimum delays, lazy load reset, pause/resume + speed change, pending motion, failed input, late frame cancellation");
            // A stable but incomplete page must be retried without injecting another wheel.
            foreach (bool recover in new[] { true, false })
            {
                now = 0; bool loading = true; int position = 0;
                var blank = new WriteableBitmap(640, 400, 96, 96, PixelFormats.Bgr32, null); blank.Freeze();
                window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
                {
                    AutoScroll = true, AutomaticPolling = false, ValidateForTest = () => true, SkipAutoGuardsForTest = true,
                    CountdownMilliseconds = 0, Now = () => now,
                    Capture = _ => position > 0 && loading ? blank : Crop(image, position),
                    SendWheel = _ => { position += 100; return true; }
                };
                window.Show(); await window.ToggleAsync();
                for (int i = 0; i < 7; i++) { now += 180; await window.PollAsync(); }
                Check(window.Running && window.InputsSent == 1 && window.Document!.Height == 400 && window.Message.Contains("等待图片"), "Uncertain frame waits without extra input or append");
                if (recover)
                {
                    loading = false;
                    for (int i = 0; i < 3; i++) { now += 180; await window.PollAsync(); }
                    Check(window.Running && window.InputsSent == 2 && window.Document!.Height == 500, "Loaded frame recovers within retry interval");
                    ScrollTests.Same(window.Document!.Build(), Crop(image, 0, 500), "Recovered output has no missing or repeated rows");
                    window.Pause();
                }
                else
                {
                    for (int i = 0; i < 15 && window.Running; i++) { now += 180; await window.PollAsync(); }
                    Check(!window.Running && window.Message.Contains("无法可靠拼接") && window.InputsSent == 1 && window.Document!.Height == 400, "Persistent uncertainty pauses and preserves preview");
                }
                previousCount = window.InputsSent;
                for (int i = 0; i < 4; i++) { now += 180; await window.PollAsync(); }
                Check(window.InputsSent == previousCount, "No input after retry pause");
                window.Close();
            }
            log.AppendLine("PASS uncertain-frame retry: no extra wheel, lazy-frame recovery, exact recovered pixels, timeout and preserved preview");
            // Countdown never injects input, and an endlessly moving page must not keep scrolling.
            now = 0; int frameNumber = 0;
            window = new(new(20, 20, 640, 400), new(0, 0, 2000, 1200))
            {
                AutoScroll = true, AutomaticPolling = false, ValidateForTest = () => true, SkipAutoGuardsForTest = true,
                CountdownMilliseconds = 2500, Now = () => now,
                Capture = _ => Crop(image, (++frameNumber % 2) * 100), SendWheel = _ => throw new InvalidOperationException("Unexpected input")
            };
            window.Show(); await window.ToggleAsync();
            now = 2499; await window.PollAsync(); Check(frameNumber == 0 && window.InputsSent == 0, "Countdown does not capture or scroll");
            for (int i = 0; i < 70 && window.Running; i++) { now += 180; await window.PollAsync(); }
            Check(!window.Running && window.Message.Contains("持续变化") && window.InputsSent == 0, "Unstable page times out without input");
            window.Close(); log.AppendLine("PASS countdown cancellation gate and continuous animation timeout");
            log.AppendLine("ALL AUTO SCROLL CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "auto-scroll-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        { window?.Close(); File.WriteAllText(Path.Combine(output, "auto-scroll-test.txt"), log + ex.ToString()); return 1; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    public static async Task<int> NativeAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Native automatic scroll: real SendInput wheel events + GDI pixels on synthetic WPF source only.\n");
        Window? page = null; ScrollCaptureWindow? capture = null;
        var oldPointer = NativeMethods.CursorPosition();
        try
        {
            var image = ScrollTests.Fixture(700, 885);
            var imageControl = new Image { Source = image, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left };
            var scroller = new ScrollViewer { Content = imageControl, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            page = new Window { Title = "Cut synthetic automatic scroll fixture", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 700, Height = 500, Left = 120, Top = 100, Content = scroller, Topmost = true };
            page.Show(); await Task.Delay(200); page.UpdateLayout();
            // Compare to an independently captured full source on this desktop. WPF/DWM
            // can round glyph colors differently from the original off-screen bitmap.
            page.Height = 885; await Task.Delay(200); page.UpdateLayout();
            var fullOrigin = scroller.PointToScreen(new Point(0, 0)); var fullDpi = VisualTreeHelper.GetDpi(scroller);
            ScrollTests.FocusFixture(page);
            var nativeReference = CaptureService.CaptureRegion(new((int)fullOrigin.X, (int)fullOrigin.Y,
                (int)(scroller.ActualWidth * fullDpi.DpiScaleX), (int)(scroller.ActualHeight * fullDpi.DpiScaleY)));
            page.Height = 500; await Task.Delay(200); page.UpdateLayout();
            var origin = scroller.PointToScreen(new Point(0, 0)); var dpi = VisualTreeHelper.GetDpi(scroller);
            var area = new PixelRect((int)origin.X, (int)origin.Y, (int)(scroller.ActualWidth * dpi.DpiScaleX), (int)(scroller.ActualHeight * dpi.DpiScaleY));
            var monitor = NativeMethods.Monitors().First(m => m.Contains(new(area.X, area.Y)));
            var hwnd = new WindowInteropHelper(page).Handle;
            foreach (var direction in Enum.GetValues<ScrollDirection>())
            foreach (var speed in Enum.GetValues<ScrollSpeed>())
            {
                imageControl.Source = image; scroller.ScrollToVerticalOffset(direction == ScrollDirection.Down ? 0 : 385); await Task.Delay(160);
                capture = new(area, monitor) { ResolveTargetForTest = () => hwnd, AutoScroll = true, Speed = speed, Direction = direction, AutomaticPolling = false, CountdownMilliseconds = 150 };
                capture.Show(); await capture.ToggleAsync(); ScrollTests.FocusFixture(page);
                SetCursorPos(area.X + 320, area.Y + 220);
                long deadline = Environment.TickCount64 + 35000;
                while (capture.Running && Environment.TickCount64 < deadline) { await Task.Delay(120); await capture.PollAsync(); }
                Check(!capture.Running && capture.Document is not null, "Auto end " + direction + "/" + speed + ": " + capture.Message);
                Check(capture.Message.Contains("已到") || capture.Message.Contains("未检测到新内容"), "End status " + capture.Message);
                Check(Math.Abs(scroller.VerticalOffset - (direction == ScrollDirection.Down ? 385 : 0)) < .01, "Real wheel reaches extent");
                Check(capture.Document!.Height == (int)Math.Round(885 * dpi.DpiScaleY), "All final rows captured: " + capture.Document.Height + ", " + capture.Message);
                if (dpi.DpiScaleY == 1 && dpi.DpiScaleX == 1) ScrollTests.Same(capture.Document.Build(), nativeReference, "Native full pixel output");
                int sent = capture.InputsSent;
                for (int i = 0; i < 4; i++) { await Task.Delay(120); await capture.PollAsync(); }
                Check(sent == capture.InputsSent, "No wheels after stop");
                if (direction == ScrollDirection.Down && speed == ScrollSpeed.Standard)
                {
                    imageControl.Source = ScrollTests.Fixture(700, 981); await Task.Delay(120);
                    await capture.ToggleAsync(); ScrollTests.FocusFixture(page); SetCursorPos(area.X + 320, area.Y + 220);
                    deadline = Environment.TickCount64 + 18000;
                    while (capture.Running && Environment.TickCount64 < deadline) { await Task.Delay(120); await capture.PollAsync(); }
                    Check(capture.Document.Height == (int)Math.Round(981 * dpi.DpiScaleY), "Native continue after newly appended content");
                    ScrollTests.Preview(capture, Path.Combine(output, "auto-scroll-native-preview.png"));
                    ExportService.SavePng(capture.Document.Build(), Path.Combine(output, "auto-scroll-native-output.png"));
                }
                log.AppendLine("PASS " + direction + "/" + speed + ": real wheel, exact extent/pixels, boundary status, no late input; " + capture.Message);
                File.WriteAllText(Path.Combine(output, "auto-scroll-native-test.txt"), log.ToString());
                capture.Close();
            }
            // Global escape, mouse exit, user wheel, foreground changes and disposal.
            imageControl.Source = ScrollTests.Fixture(700, 2600); scroller.ScrollToVerticalOffset(0); await Task.Delay(120);
            capture = new(area, monitor) { ResolveTargetForTest = () => hwnd, AutoScroll = true, AutomaticPolling = false, CountdownMilliseconds = 0 };
            capture.Show();
            async Task Arm()
            {
                await capture.ToggleAsync(); ScrollTests.FocusFixture(page); SetCursorPos(area.X + 320, area.Y + 220);
                for (int i = 0; i < 3; i++) { await Task.Delay(120); await capture.PollAsync(); }
                Check(capture.Running, "Rearmed: " + capture.Message);
            }
            await capture.ToggleAsync(); ScrollTests.FocusFixture(page); SetCursorPos(area.X + 320, area.Y + 220);
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            try { await Task.Delay(80); await capture.PollAsync(); Check(!capture.Running && capture.InputsSent == 0, "Held Ctrl during countdown cannot cause wheel zoom"); }
            finally { keybd_event(0x11, 0, 2, UIntPtr.Zero); }
            await Task.Delay(80);
            await Arm();
            keybd_event(0x1B, 0, 0, UIntPtr.Zero); keybd_event(0x1B, 0, 2, UIntPtr.Zero); await Task.Delay(160);
            Check(!capture.Running && capture.IsVisible, "Global Esc stops without closing preview");
            int before = capture.InputsSent; await capture.PollAsync(); Check(before == capture.InputsSent, "Esc prevents late wheel");
            await Arm(); SetCursorPos(area.Right + 50, area.Bottom + 50); await capture.PollAsync();
            Check(!capture.Running && capture.Message.Contains("鼠标"), "Leaving viewport pauses");
            await Arm();
            mouse_event(0x0800, 0, 0, unchecked((uint)-120), UIntPtr.Zero); await Task.Delay(180);
            Check(!capture.Running && capture.Message.Contains("手动操作"), "External wheel takeover pauses");
            await Arm(); ScrollTests.FocusFixture(capture); await capture.PollAsync();
            Check(!capture.Running, "Focus loss pauses");
            await Arm();
            Button? FindPause(DependencyObject parent)
            {
                if (parent is Button button && Equals(button.Content, "暂停")) return button;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                { var found = FindPause(VisualTreeHelper.GetChild(parent, i)); if (found is not null) return found; }
                return null;
            }
            var pauseButton = FindPause(capture)!; Check(pauseButton is not null, "Pause button exists");
            var buttonPoint = pauseButton!.PointToScreen(new Point(pauseButton.ActualWidth / 2, pauseButton.ActualHeight / 2));
            SetCursorPos((int)buttonPoint.X, (int)buttonPoint.Y);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); await Task.Delay(250);
            Check(!capture.Running && capture.IsVisible, "Pause click is not misinterpreted as immediate resume");
            // Close while active, then ensure the retained test reference never issues another wheel.
            await Arm(); before = capture.InputsSent; capture.Close();
            await Task.Delay(180); await capture.PollAsync(); Check(before == capture.InputsSent, "Close cancels input");
            using (var key = new HotkeyService()) Check(key.TryRegister(0, 0x1B), "Escape registration released");
            page.Close();
            log.AppendLine("PASS held modifier guard, global Esc, mouse exit, external wheel takeover, focus loss, pause-button click, close cancellation and hook/hotkey cleanup");
            log.AppendLine("ALL NATIVE AUTO SCROLL CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "auto-scroll-native-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        { capture?.Close(); page?.Close(); File.WriteAllText(Path.Combine(output, "auto-scroll-native-test.txt"), log + ex.ToString()); return 1; }
        finally { SetCursorPos(oldPointer.X, oldPointer.Y); }
    }
}
