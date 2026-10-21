using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class SelfTests
{
    private static readonly string Output = Path.Combine(Environment.CurrentDirectory, "artifacts");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static BitmapSource Solid(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = b; data[i + 1] = g; data[i + 2] = r; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, data, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
    public static int Run()
    {
        Directory.CreateDirectory(Output);
        var log = new StringBuilder();
        try
        {
            var bounds = new PixelRect(-200, -100, 600, 400);
            var c = new SelectionController();
            c.Begin(new(100, 80), 5); c.Update(new(-80, -40), bounds); c.End();
            Check(c.Selection == new PixelRect(-80, -40, 180, 120), "Reverse drag / negative origin");
            log.AppendLine("PASS reverse drag and negative coordinates");
            c.Begin(new(0, 0), 5); c.Update(new(1000, 1000), bounds); c.End();
            Check(c.Selection == new PixelRect(220, 180, 180, 120), "Move must preserve size and clamp to desktop");
            log.AppendLine("PASS move clamping preserves dimensions");
            c.Begin(new(220, 180), 5); c.Update(new(390, 290), bounds); c.End();
            Check(c.Selection == new PixelRect(390, 290, 10, 10), "Corner resizing");
            c.Begin(new(390, 290), 2); c.Update(new(400, 300), bounds); c.End();
            Check(c.Selection.IsEmpty, "Zero-size resize");
            c.Clear(); c.Begin(new(-150, -50), 5); c.Update(new(-100, 0), bounds); c.End();
            c.Begin(new(-150, -25), 5); c.Update(new(-60, -25), bounds); c.End();
            Check(c.Selection == new PixelRect(-100, -50, 40, 50), "Resize across opposite edge");
            c.Clear(); Check(c.Selection.IsEmpty && !c.IsDragging, "Clear state");
            log.AppendLine("PASS corner resizing, edge inversion, zero area and clear");
            var snapshot = new DesktopSnapshot(new[] {
                new MonitorSnapshot(new(-4, -2, 4, 4), Solid(4, 4, 0, 0, 255)),
                new MonitorSnapshot(new(0, 0, 4, 4), Solid(4, 4, 0, 255, 0))
            });
            var crop = snapshot.Crop(new(-2, -1, 4, 4));
            Check(crop.PixelWidth == 4 && crop.PixelHeight == 4, "Crop dimensions");
            var bytes = new byte[64]; crop.CopyPixels(bytes, 16, 0);
            Check(bytes[2] == 255, "Left screen red pixel");
            Check(bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0, "Monitor gap black pixel");
            Check(bytes[25] == 255 && bytes[26] == 0, "Right screen green pixel");
            log.AppendLine("PASS exact multi-monitor pixels, negative origin and black gaps");
            string path = Path.Combine(Output, "crop-test.png");
            ExportService.SavePng(crop, path);
            ExportService.SavePng(crop, path);
            using (var stream = File.OpenRead(path))
            {
                var decoded = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Check(decoded.PixelWidth == 4 && decoded.PixelHeight == 4, "PNG round-trip");
                var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgr32, null, 0);
                var decodedBytes = new byte[64]; converted.CopyPixels(decodedBytes, 16, 0);
                for (int i = 0; i < bytes.Length; i++) if (i % 4 != 3) Check(bytes[i] == decodedBytes[i], "PNG exact RGB");
            }
            Check(!Directory.EnumerateFiles(Output, "*.tmp").Any(), "Temporary files cleaned");
            log.AppendLine("PASS PNG encoding, overwrite, exact pixels and temporary cleanup");
            using (var first = new HotkeyService())
            using (var second = new HotkeyService())
            {
                Check(first.TryRegister(7, 0x87), "Test hotkey registration; Win32 error " + first.LastError);
                Check(!second.TryRegister(7, 0x87), "Hotkey conflict detection");
                Check(first.TryRegister(7, 0x86), "Hotkey replacement");
                Check(second.TryRegister(7, 0x87), "Old hotkey released after replacement");
            }
            log.AppendLine("PASS native hotkey registration, conflict detection and replacement");
            log.AppendLine("ALL CHECKS PASSED");
            File.WriteAllText(Path.Combine(Output, "self-test.txt"), log.ToString());
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Output, "self-test.txt"), log + ex.ToString()); return 1; }
    }
    public static async Task<int> SmokeAsync()
    {
        Directory.CreateDirectory(Output);
        CaptureSession? session = null;
        try
        {
            var snapshot = CaptureService.Capture();
            session = new CaptureSession(snapshot);
            session.Show();
            await Task.Delay(350);
            Check(Application.Current.Windows.OfType<OverlayWindow>().Count() == snapshot.Monitors.Count, "Overlay count");
            var log = new StringBuilder();
            foreach (var window in Application.Current.Windows.OfType<OverlayWindow>())
            {
                var origin = window.PointToScreen(new Point(0, 0));
                var end = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
                var r = window.Monitor.Bounds;
                Check(Math.Abs(origin.X - r.X) < 2 && Math.Abs(origin.Y - r.Y) < 2 && Math.Abs(end.X - r.Right) < 2 && Math.Abs(end.Y - r.Bottom) < 2, "Overlay physical bounds");
                log.AppendLine($"PASS overlay {r}: logical {window.ActualWidth} × {window.ActualHeight}; physical bounds match");
            }
            // Synthetic content keeps the review artifact free of the user's desktop.
            session.Abort(); session = null;
            var monitor = new MonitorSnapshot(new PixelRect(0, 0, 1000, 680), Solid(1000, 680, 100, 74, 42));
            var fixture = new CaptureSession(new DesktopSnapshot(new[] { monitor }));
            fixture.Selection.Begin(new(170, 130), 5); fixture.Selection.Update(new(820, 480), fixture.Snapshot.Bounds); fixture.Selection.End();
            var preview = new OverlayWindow(fixture, monitor);
            preview.Show();
            await Task.Delay(100);
            preview.Refresh(true); preview.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(preview);
            var image = new RenderTargetBitmap((int)Math.Round(preview.ActualWidth * dpi.DpiScaleX), (int)Math.Round(preview.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            image.Render(preview);
            ExportService.SavePng(image, Path.Combine(Output, "overlay-preview.png"));
            fixture.Abort();
            // The preview window is not registered in fixture.Show's window list.
            preview.Close();
            var settings = new MainWindow((App)Application.Current, new HotkeySettings());
            settings.SetStatus("当前快捷键：Ctrl + Alt + A");
            settings.Show();
            await Task.Delay(100);
            settings.UpdateLayout();
            var settingsDpi = VisualTreeHelper.GetDpi(settings);
            var settingsImage = new RenderTargetBitmap((int)Math.Ceiling(settings.ActualWidth * settingsDpi.DpiScaleX), (int)Math.Ceiling(settings.ActualHeight * settingsDpi.DpiScaleY), settingsDpi.PixelsPerInchX, settingsDpi.PixelsPerInchY, PixelFormats.Pbgra32);
            settingsImage.Render(settings);
            ExportService.SavePng(settingsImage, Path.Combine(Output, "settings-preview.png"));
            settings.Hide();
            log.AppendLine("PASS real desktop acquisition and synthetic overlay rendering");
            File.WriteAllText(Path.Combine(Output, "smoke-test.txt"), log.ToString());
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Output, "smoke-test.txt"), ex.ToString()); return 1; }
        finally { session?.Abort(); }
    }
}
