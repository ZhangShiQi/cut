using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class WindowSelectionTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static BitmapSource Background(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 90; pixels[i + 1] = 60; pixels[i + 2] = 30; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze(); return bitmap;
    }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(output);
        var log = new StringBuilder();
        var testWindows = new List<Window>();
        CaptureSession? session = null;
        try
        {
            var desktop = new DesktopSnapshot(new[] {
                new MonitorSnapshot(new(-400, -200, 400, 400), Background(400, 400)),
                new MonitorSnapshot(new(0, 0, 800, 600), Background(800, 600))
            });
            var front = new PixelRect(100, 100, 300, 200);
            var back = new PixelRect(40, 40, 500, 400);
            var clipped = new PixelRect(-500, -250, 220, 160);
            var candidates = new[] { new WindowCandidate(new IntPtr(1), front), new WindowCandidate(new IntPtr(2), back), new WindowCandidate(new IntPtr(3), clipped) };
            PixelRect Find(PixelPoint p) => WindowDetectionService.FindAt(p, candidates, desktop);
            Check(Find(new(150, 150)) == front, "Front window must win overlap");
            Check(Find(new(50, 50)) == back, "Exposed back window");
            Check(Find(new(-350, -150)) == new PixelRect(-400, -200, 120, 110), "Negative origin/offscreen clipping");
            Check(Find(new(700, 500)) == desktop.Monitors[1].Bounds, "Desktop fallback should select current monitor");
            Check(Find(new(100, -100)).IsEmpty, "No selection in gap between monitors");
            log.AppendLine("PASS frontmost picking, exposed background window, negative coordinates, clipping and monitor fallback");

            var selection = new SelectionController();
            var interaction = new CaptureInteraction(selection, desktop.Bounds, Find);
            interaction.Hover(new(150, 150));
            Check(interaction.IsPreview && selection.Selection.IsEmpty && interaction.Preview == front, "Hover must not commit selection");
            interaction.Press(new(150, 150), 6, 4, 4);
            interaction.Move(new(152, 151)); interaction.Release(new(152, 151));
            Check(selection.Selection == front && !interaction.IsPreview, "Tiny click jitter must select window");
            interaction.Hover(new(60, 60)); Check(selection.Selection == front, "Committed selection must stop following mouse");
            interaction.Press(new(160, 160), 6, 4, 4); interaction.Move(new(190, 190)); interaction.Release(new(190, 190));
            Check(selection.Selection == new PixelRect(130, 130, 300, 200), "Selected window can be moved");
            interaction.Press(new(430, 330), 6, 4, 4); interaction.Release(new(450, 350));
            Check(selection.Selection == new PixelRect(130, 130, 320, 220), "Selected window can be resized");
            interaction.Reset(new(150, 150));
            interaction.Press(new(150, 150), 6, 4, 4); interaction.Move(new(50, 70)); interaction.Release(new(50, 70));
            Check(selection.Selection == new PixelRect(50, 70, 100, 80), "Drag must make custom rectangle, not move the hover target");
            interaction.Reset(new(150, 150)); interaction.Press(new(150, 150), 6, 4, 4); interaction.Release(new(-50, -50));
            Check(selection.Selection == new PixelRect(-50, -50, 200, 200), "Release position handles missing intermediate move events");
            interaction.Reset(new(150, 150)); interaction.Press(new(150, 150), 6, 4, 4); interaction.LoseCapture();
            Check(selection.Selection.IsEmpty, "Lost capture must not commit pending click");
            interaction.Reset(new(150, 150)); Check(interaction.ConfirmPreview() && selection.Selection == front, "Keyboard confirms preview");
            log.AppendLine("PASS hover/click distinction, jitter threshold, free drag, move/resize, reset, lost capture and keyboard confirmation");

            Window MakeWindow(string title, double left, double top, Brush color)
            {
                var w = new Window { Title = title, Width = 440, Height = 280, Left = left, Top = top, Topmost = true, ShowInTaskbar = false, Background = color, Content = new TextBlock { Text = title, FontSize = 25, Margin = new Thickness(24) } };
                testWindows.Add(w); w.Show(); return w;
            }
            var nativeBack = MakeWindow("Cut test · background", 80, 80, Brushes.LightSteelBlue);
            var nativeFront = MakeWindow("Cut test · foreground", 170, 140, Brushes.MintCream);
            await Task.Delay(150);
            var backHandle = new WindowInteropHelper(nativeBack).Handle;
            var frontHandle = new WindowInteropHelper(nativeFront).Handle;
            var recorded = WindowDetectionService.Capture(0);
            var actualFront = WindowDetectionService.GetFrameBounds(frontHandle);
            Check(recorded.Any(w => w.Handle == frontHandle && w.Bounds == actualFront), "Native DWM frame must be recorded");
            Check(recorded.ToList().FindIndex(w => w.Handle == frontHandle) < recorded.ToList().FindIndex(w => w.Handle == backHandle), "Native enumeration must preserve Z order");
            Check(!WindowDetectionService.Capture().Any(w => w.Handle == frontHandle || w.Handle == backHandle), "Own process must be excluded");
            nativeFront.Hide();
            Check(!WindowDetectionService.Capture(0).Any(w => w.Handle == frontHandle), "Hidden window excluded");
            nativeFront.Show(); nativeFront.WindowState = WindowState.Minimized;
            await Task.Delay(80);
            Check(!WindowDetectionService.Capture(0).Any(w => w.Handle == frontHandle), "Minimized window excluded");
            nativeFront.WindowState = WindowState.Normal; nativeFront.Activate();
            await Task.Delay(80);
            recorded = WindowDetectionService.Capture(0);
            actualFront = WindowDetectionService.GetFrameBounds(frontHandle);
            var realDesktop = CaptureService.Capture();
            var point = new PixelPoint(actualFront.X + actualFront.Width / 2, actualFront.Y + actualFront.Height / 2);
            Check(WindowDetectionService.FindAt(point, recorded, realDesktop) == actualFront.Intersect(realDesktop.Bounds), "Native overlapping windows pick foreground");
            session = new CaptureSession(realDesktop, recorded);
            session.Show();
            session.Interaction.Hover(point); session.Update();
            Check(session.Interaction.Preview == actualFront.Intersect(realDesktop.Bounds), "Capture overlays must not replace frozen target");
            session.Interaction.Press(point, 6, 4, 4); session.Interaction.Release(point); session.EndDrag(point);
            Check(session.Selection.Selection == actualFront.Intersect(realDesktop.Bounds), "Click commits native target");
            session.RightClick();
            Check(session.Selection.Selection.IsEmpty && session.Interaction.IsPreview, "Right click restores hover mode");
            session.Abort(); session = null;
            log.AppendLine("PASS native DWM bounds, Z order, own/hidden/minimized exclusions and selection with overlays present");

            // Reproduce GPU-overlay behavior: a large topmost HWND can be marked
            // visible while clicks pass through it to the actual application.
            // WinForms owns WS_EX_LAYERED here; WPF's compositor can rewrite that
            // style for non-transparent WPF windows during WM_STYLECHANGING.
            using var layer = new System.Windows.Forms.Form { Text = "Cut test · GPU overlay", Width = 1000, Height = 680, Left = 0, Top = 0, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, BackColor = System.Drawing.Color.SteelBlue, Opacity = 0.3 };
            layer.Show();
            var layerHandle = layer.Handle;
            int baseStyle = NativeMethods.GetWindowLong(layerHandle, -20);
            NativeMethods.SetWindowLong(layerHandle, -20, baseStyle | 0x00080000 | 0x00000020);
            Check(NativeMethods.SetLayeredWindowAttributes(layerHandle, 0, 80, 2), "Set click-through layered test window");
            await Task.Delay(80);
            var throughLayer = WindowDetectionService.Capture(0);
            Check(!throughLayer.Any(w => w.Handle == layerHandle), "Click-through GPU overlay must be excluded");
            Check(WindowDetectionService.FindAt(point, throughLayer, realDesktop) == actualFront.Intersect(realDesktop.Bounds), "GPU overlay must not steal underlying window selection");
            log.AppendLine("PASS click-through topmost GPU-overlay regression");

            NativeMethods.SetWindowLong(layerHandle, -20, (baseStyle | 0x00080000) & ~0x00000020);
            Check(NativeMethods.SetLayeredWindowAttributes(layerHandle, 0, 0, 2), "Set fully transparent test window");
            Check(!WindowDetectionService.Capture(0).Any(w => w.Handle == layerHandle), "Zero-alpha layer must be excluded even without click-through");
            Check(NativeMethods.SetLayeredWindowAttributes(layerHandle, 0, 180, 2), "Set interactive translucent test window");
            Check(WindowDetectionService.Capture(0).Any(w => w.Handle == layerHandle), "Interactive translucent application must remain selectable");
            layer.Close();
            log.AppendLine("PASS zero-alpha exclusion while preserving interactive translucent windows");
            foreach (var window in testWindows) window.Close(); testWindows.Clear();

            // A synthetic desktop keeps the preview free of personal screen content.
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(31, 49, 72)), null, new Rect(0, 0, 1000, 680));
                dc.DrawRoundedRectangle(Brushes.WhiteSmoke, null, new Rect(120, 90, 650, 450), 10, 10);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(220, 234, 242)), null, new Rect(120, 90, 650, 42));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 123, 104)), null, new Rect(155, 170, 210, 170));
                dc.DrawRectangle(Brushes.LightGray, null, new Rect(395, 175, 320, 16));
                dc.DrawRectangle(Brushes.LightGray, null, new Rect(395, 210, 250, 12));
                dc.DrawRectangle(Brushes.LightGray, null, new Rect(395, 240, 285, 12));
            }
            var background = new RenderTargetBitmap(1000, 680, 96, 96, PixelFormats.Pbgra32);
            background.Render(drawing); background.Freeze();
            var synthetic = new DesktopSnapshot(new[] { new MonitorSnapshot(new(0, 0, 1000, 680), background) });
            session = new CaptureSession(synthetic, new[] { new WindowCandidate(new IntPtr(42), new(120, 90, 650, 450)) });
            session.Show(); session.Interaction.Hover(new(200, 200)); session.Update();
            await Task.Delay(80);
            var preview = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            preview.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(preview);
            var image = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(preview.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(preview);
            ExportService.SavePng(image, Path.Combine(output, "window-hover-preview.png"));
            session.Abort(); session = null;
            log.AppendLine("PASS synthetic preselection rendering");
            log.AppendLine("ALL WINDOW SELECTION CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "window-test.txt"), log.ToString());
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "window-test.txt"), log + ex.ToString()); return 1; }
        finally { session?.Abort(); foreach (var window in testWindows) window.Close(); }
    }
}
