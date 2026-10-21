using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class QrTests
{
    private const string Url = "https://example.com/cut-test?source=qr&value=42";
    private const string UnicodeText = "Cut 二维码测试\n第二行：你好，世界！";
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static BitmapSource Load(string name)
    {
        using var stream = File.OpenRead(Path.Combine(Environment.CurrentDirectory, "Tests", "Fixtures", name + ".png"));
        var image = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        image.Freeze();
        return image;
    }
    private static void SavePreview(Window window, string name)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window);
        ExportService.SavePng(image, Path.Combine(Environment.CurrentDirectory, "artifacts", name + ".png"));
    }
    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts");
        Directory.CreateDirectory(output);
        var log = new StringBuilder();
        try
        {
            foreach (var test in new[] { ("url", Url), ("unicode", UnicodeText), ("rotated", Url), ("inverted", Url), ("small", "small QR") })
            {
                var timer = Stopwatch.StartNew();
                var found = await QrCodeService.RecognizeAsync(Load(test.Item1), CancellationToken.None);
                Check(found.Count == 1 && found[0].Text == test.Item2, test.Item1 + " exact payload");
                log.AppendLine($"PASS {test.Item1}: exact payload ({timer.ElapsedMilliseconds} ms)");
            }
            var results = await QrCodeService.RecognizeAsync(Load("multiple"), CancellationToken.None);
            Check(results.Count == 3 && results.Select(r => r.Text).ToHashSet().SetEquals(new[] { Url, UnicodeText, "https://example.org/second" }), "Multiple QR, mixed polarity and duplicate content");
            log.AppendLine("PASS multiple QR, mixed normal/inverted, same-content deduplication");
            Check((await QrCodeService.RecognizeAsync(Load("blank"), CancellationToken.None)).Count == 0, "No QR result");
            log.AppendLine("PASS blank image returns no results");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                bool observed = false;
                try { await QrCodeService.RecognizeAsync(Load("url"), cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "Cancelled work must not start");
            }
            var blank = Load("blank");
            var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, blank.PixelWidth, blank.PixelHeight), blank) });
            var session = new CaptureSession(snapshot);
            var owner = new OverlayWindow(session, snapshot.Monitors[0]);
            session.Selection.Begin(new(0, 0), 1); session.Selection.Update(new(blank.PixelWidth, blank.PixelHeight), snapshot.Bounds); session.Selection.End();
            var pending = session.RecognizeAsync(owner);
            Check(session.IsRecognizing, "Recognition busy state");
            session.Cancel();
            Check(session.IsFinished, "Esc cancellation must finish immediately");
            await pending;
            Check(!session.Busy && !Application.Current.Windows.OfType<QrResultWindow>().Any(w => w.IsVisible), "No late popup after cancel");
            owner.Close();
            log.AppendLine("PASS queued cancellation, immediate session cancel, no late popup");
            foreach (string invalid in new[] { "plain text", "javascript:alert(1)", "file:///C:/Windows/notepad.exe", "mailto:test@example.org", "https://example.org\nrun", "https:\\example.org", "https://" })
                Check(LinkService.GetWebUri(invalid) is null, "Invalid URL accepted: " + invalid);
            foreach (string valid in new[] { Url, "http://example.org/path?q=中文", " HTTPS://EXAMPLE.ORG/test " })
            {
                var info = LinkService.CreateStartInfo(valid);
                Check(info.UseShellExecute && info.Arguments.Length == 0 && info.FileName.StartsWith("http", StringComparison.OrdinalIgnoreCase), "Browser launch target");
            }
            log.AppendLine("PASS HTTP/HTTPS parsing and shell launch target; other schemes remain text");

            // Exercise actual result-window actions with captured side effects. Tests do
            // not replace the user's clipboard or open unrelated browser tabs.
            string? copied = null, opened = null;
            var items = new[] { new QrCodeResult(Url), new QrCodeResult(UnicodeText), new QrCodeResult("https://example.org/second") };
            var copyWindow = new QrResultWindow(items, (text, _) => { copied = text; return Task.CompletedTask; }, text => opened = text);
            copyWindow.Show();
            copyWindow.SelectResult(1);
            await Task.Delay(80);
            SavePreview(copyWindow, "qr-results-preview");
            await copyWindow.CopySelectedAsync();
            Check(copied == UnicodeText && opened is null && copyWindow.ActionCompleted && !copyWindow.IsVisible, "Copy selected unicode content and close");
            var openWindow = new QrResultWindow(items, (_, _) => throw new Exception("Unexpected copy"), text => opened = text);
            openWindow.Show(); openWindow.SelectResult(1); openWindow.OpenSelected();
            Check(opened is null && openWindow.IsVisible, "Plain text cannot open a link");
            openWindow.SelectResult(2); openWindow.OpenSelected();
            Check(opened == "https://example.org/second" && openWindow.ActionCompleted && !openWindow.IsVisible, "Open selected link and close");
            var failureWindow = new QrResultWindow(items, (_, _) => throw new IOException("test clipboard busy"), _ => throw new IOException("test browser unavailable"));
            failureWindow.Show(); await failureWindow.CopySelectedAsync();
            Check(failureWindow.IsVisible && !failureWindow.ActionCompleted, "Failed copy must preserve result");
            failureWindow.OpenSelected();
            Check(failureWindow.IsVisible && !failureWindow.ActionCompleted, "Failed open must preserve result");
            failureWindow.Close();
            log.AppendLine("PASS result selection, Unicode copy action, link action, and error recovery (side effects captured)");

            // Drive the real session to a modal result window, then close it and
            // verify selection preservation and the absence of a stale Busy state.
            var qrImage = Load("multiple");
            var qrSnapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, qrImage.PixelWidth, qrImage.PixelHeight), qrImage) });
            var qrSession = new CaptureSession(qrSnapshot);
            qrSession.Show();
            var qrOwner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            qrSession.Selection.Begin(new(0, 0), 1); qrSession.Selection.Update(new(qrImage.PixelWidth, qrImage.PixelHeight), qrSnapshot.Bounds); qrSession.EndDrag(new(10, 10));
            var originalSelection = qrSession.Selection.Selection;
            var closeResult = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            bool resultShown = false;
            var deadline = Stopwatch.StartNew();
            closeResult.Tick += (_, _) =>
            {
                var popup = Application.Current.Windows.OfType<QrResultWindow>().FirstOrDefault(w => w.IsVisible);
                if (popup is not null) { resultShown = true; popup.Close(); closeResult.Stop(); }
                else if (deadline.Elapsed > TimeSpan.FromSeconds(15)) { qrSession.Abort(); closeResult.Stop(); }
            };
            closeResult.Start();
            await qrSession.RecognizeAsync(qrOwner);
            closeResult.Stop();
            Check(resultShown && !qrSession.IsFinished && !qrSession.Busy && qrSession.Selection.Selection == originalSelection, "Closing results returns to selection");
            qrOwner.Refresh(true); SavePreview(qrOwner, "qr-overlay-preview");
            qrSession.Abort();
            log.AppendLine("PASS full capture-to-results flow and return to unchanged selection");

            var emptySession = new CaptureSession(snapshot);
            emptySession.Show();
            var emptyOwner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            emptySession.Selection.Begin(new(0, 0), 1); emptySession.Selection.Update(new(blank.PixelWidth, blank.PixelHeight), snapshot.Bounds); emptySession.EndDrag(new(10, 10));
            var emptySelection = emptySession.Selection.Selection;
            await emptySession.RecognizeAsync(emptyOwner);
            Check(!emptySession.IsFinished && !emptySession.Busy && emptySession.Selection.Selection == emptySelection && emptySession.StatusMessage.StartsWith("未找到二维码"), "No-result session must preserve selection and show feedback");
            emptySession.Abort();
            log.AppendLine("PASS no-result session preserves selection and allows retry");

            string? sessionCopied = null;
            var successSession = new CaptureSession(qrSnapshot, found => new QrResultWindow(found, (text, _) => { sessionCopied = text; return Task.CompletedTask; }, _ => throw new Exception("Unexpected browser launch")));
            successSession.Show();
            var successOwner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
            successSession.Selection.Begin(new(0, 0), 1); successSession.Selection.Update(new(qrImage.PixelWidth, qrImage.PixelHeight), qrSnapshot.Bounds); successSession.EndDrag(new(10, 10));
            var copyResult = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            deadline.Restart();
            copyResult.Tick += async (_, _) =>
            {
                var popup = Application.Current.Windows.OfType<QrResultWindow>().FirstOrDefault(w => w.IsVisible);
                if (popup is not null) { copyResult.Stop(); await popup.CopySelectedAsync(); }
                else if (deadline.Elapsed > TimeSpan.FromSeconds(15)) { successSession.Abort(); copyResult.Stop(); }
            };
            copyResult.Start();
            await successSession.RecognizeAsync(successOwner);
            copyResult.Stop();
            Check(successSession.IsFinished && sessionCopied is not null && !Application.Current.Windows.OfType<OverlayWindow>().Any(w => w.IsVisible), "Successful result copy must close all overlays");
            log.AppendLine("PASS successful result action completes session and closes all overlays");
            log.AppendLine("ALL QR CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "qr-test.txt"), log.ToString());
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "qr-test.txt"), log + ex.ToString()); return 1; }
    }
}
