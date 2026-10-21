using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class OcrTests
{
    private static readonly string Output = Path.Combine(Environment.CurrentDirectory, "artifacts");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Sample(string Name, string[] Lines, int FontSize, bool Dark = false, int Width = 1000);
    internal static BitmapSource Render(string[] lines, int size = 28, bool dark = false, int width = 1000)
    {
        int height = Math.Max(150, 64 + lines.Length * (size + 22));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(dark ? new SolidColorBrush(Color.FromRgb(28, 32, 40)) : Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < lines.Length; i++)
                dc.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), size, dark ? Brushes.White : Brushes.Black, 1), new Point(28, 24 + i * (size + 22)));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
    private static void SavePreview(Window window, string name)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window);
        ExportService.SavePng(image, Path.Combine(Output, name + ".png"));
    }
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static double ErrorRate(string expected, string actual)
    {
        expected = Normalize(expected); actual = Normalize(actual);
        int[] previous = Enumerable.Range(0, actual.Length + 1).ToArray();
        for (int i = 1; i <= expected.Length; i++)
        {
            int[] current = new int[actual.Length + 1]; current[0] = i;
            for (int j = 1; j <= actual.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (expected[i - 1] == actual[j - 1] ? 0 : 1));
            previous = current;
        }
        return (double)previous[^1] / Math.Max(1, expected.Length);
    }
    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory(Output);
        var log = new StringBuilder();
        try
        {
            log.AppendLine("Cut offline OCR tests · PP-OCRv5 mobile / ONNX CPU");
            var samples = new[]
            {
                new Sample("mixed", new[] { "截图文字识别 Cut OCR 2026", "支持中文 English 混排和数字 123456", "编辑结果后复制文本" }, 28),
                new Sample("dark", new[] { "深色背景 Dark Mode", "离线识别 Privacy First 2026" }, 22, true),
                new Sample("small", new[] { "小字号测试 Small text 12345", "中英文混排 Copy and edit" }, 14),
                new Sample("english", new[] { "Hello World! Cut OCR version 0.4.0", "Email: hello@example.com", "Price: 128.50 / Order 20261021" }, 24),
                new Sample("punctuation", new[] { "项目名称：文字识别", "测试编号：ABC-123，数量：42。" }, 26),
                new Sample("multi-line", Enumerable.Range(1, 8).Select(i => $"第 {i} 行文字 Line {i} 测试内容").ToArray(), 20),
                new Sample("wide", new[] { "大选区 Wide screenshot 2800" }, 26, false, 2800),
                new Sample("tile-boundary", new[] { string.Concat(Enumerable.Repeat("中英文混排 English text 12345 ", 7)) }, 20, false, 3000)
            };
            var timings = new List<long>();
            BitmapSource? mixedImage = null; OcrResult? mixedResult = null;
            foreach (var sample in samples)
            {
                var image = Render(sample.Lines, sample.FontSize, sample.Dark, sample.Width);
                ExportService.SavePng(image, Path.Combine(Output, "ocr-fixture-" + sample.Name + ".png"));
                var stopwatch = Stopwatch.StartNew();
                var result = await OcrService.RecognizeAsync(image, CancellationToken.None);
                stopwatch.Stop(); timings.Add(stopwatch.ElapsedMilliseconds);
                double error = ErrorRate(string.Join("\n", sample.Lines), result.Text);
                log.AppendLine($"{sample.Name}: {stopwatch.ElapsedMilliseconds} ms, CER ignoring whitespace {error:P2}, {result.Lines.Count} regions");
                log.AppendLine(result.Text);
                Check(error <= .08, "Recognition quality regression: " + sample.Name);
                Check(result.Lines.All(l => !l.Bounds.IsEmpty && l.Bounds.X >= 0 && l.Bounds.Y >= 0 && l.Bounds.Right <= image.PixelWidth && l.Bounds.Bottom <= image.PixelHeight), "Bounds outside image");
                if (sample.Name == "mixed") { mixedImage = image; mixedResult = result; }
            }
            log.AppendLine($"PASS {samples.Length} recognition fixtures; cold {timings[0]} ms, warm median {timings.Skip(1).OrderBy(x => x).ElementAt(timings.Count / 2)} ms");
            foreach (bool dark in new[] { false, true })
                Check((await OcrService.RecognizeAsync(Render(Array.Empty<string>(), dark: dark), CancellationToken.None)).Lines.Count == 0, "Blank image must not invent text");
            log.AppendLine("PASS white / dark blank images");
            File.WriteAllText(Path.Combine(Output, "ocr-native-build.txt"), OpenCvSharp.Cv2.GetBuildInformation());
            foreach (var name in new[] { "onnxruntime.dll", "OpenCvSharpExtern.dll", "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            {
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, name))) continue;
                var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
                Check(module is not null && Path.GetDirectoryName(module.FileName)!.Equals(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Native library must load from package: " + name);
                log.AppendLine("PASS native library loaded from package: " + name);
            }

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                bool observed = false;
                try { await OcrService.RecognizeAsync(mixedImage!, cancelled.Token); }
                catch (OperationCanceledException) { observed = true; }
                Check(observed, "Pre-cancelled OCR must not run");
            }
            using (var cancelled = new CancellationTokenSource())
            {
                var pending = OcrService.RecognizeAsync(Render(Enumerable.Repeat("取消识别 Cancellation test", 18).ToArray(), 28, width: 2800), cancelled.Token);
                await Task.Delay(25); cancelled.Cancel();
                bool observed = false;
                try { await pending; } catch (OperationCanceledException) { observed = true; }
                Check(observed, "In-flight cancellation");
            }
            Check((await OcrService.RecognizeAsync(mixedImage!, CancellationToken.None)).Lines.Count > 0, "Inference must work after cancelled RunOptions");
            log.AppendLine("PASS pre-cancelled / in-flight cancellation and subsequent inference");

            var ordered = PaddleOcrEngine.OrderLines(new[] { new OcrLine("R2", new(400, 60, 100, 20), 1), new OcrLine("L2", new(20, 60, 100, 20), 1), new OcrLine("R1", new(400, 20, 100, 20), 1), new OcrLine("L1", new(20, 20, 100, 20), 1) });
            Check(string.Join(",", ordered.Select(l => l.Text)) == "L1,L2,R1,R2", "Column reading order");
            foreach (int length in new[] { 1, 1536, 1537, 2800, 9000 })
            {
                var starts = PaddleOcrEngine.TileStarts(length).ToArray();
                Check(starts.Distinct().Count() == starts.Length && starts[0] == 0 && starts[^1] + 1536 >= length, "Tile coverage");
            }
            string missing = Path.Combine(Output, "ocr-missing-models");
            bool missingCaught = false;
            try { PaddleOcrEngine.VerifyModel(missing, "absent.onnx", "bad"); } catch (FileNotFoundException) { missingCaught = true; }
            Check(missingCaught, "Missing model must be actionable");
            string corruptFile = Path.Combine(Output, "ocr-corrupt-test.onnx"); File.WriteAllText(corruptFile, "not a model");
            bool corruptCaught = false;
            try { PaddleOcrEngine.VerifyModel(Output, "ocr-corrupt-test.onnx", new string('0', 64)); } catch (InvalidDataException) { corruptCaught = true; }
            Check(corruptCaught, "Corrupted model must not load");
            log.AppendLine("PASS reading order, tile coverage, missing/corrupted model errors");

            string? copied = null;
            var dialog = new OcrResultWindow(mixedImage!, mixedResult!, (text, _) => { copied = text; return Task.CompletedTask; });
            dialog.Show(); await Task.Delay(80);
            SavePreview(dialog, "ocr-results-preview");
            dialog.EditedText = "已编辑 Edited 42\r\n第二行中文 English";
            dialog.SelectText(0, 12);
            dialog.CopySelectionCommand(); await Task.Yield();
            Check(copied == dialog.EditedText[..12] && dialog.IsVisible && !dialog.ActionCompleted, "Copy selected edited text must preserve dialog");
            string original = dialog.EditedText;
            dialog.MergeLines(); Check(!dialog.EditedText.Contains('\n'), "Merge edited text");
            dialog.UndoEdit(); Check(dialog.EditedText == original, "Merge is undoable");
            await dialog.CopyAsync(false);
            Check(copied == original && dialog.ActionCompleted && !dialog.IsVisible, "Copy all uses edited content");
            var failure = new OcrResultWindow(mixedImage!, mixedResult!, (_, _) => throw new IOException("Clipboard busy (test)"));
            failure.Show(); failure.EditedText = "保留我的修改";
            await failure.CopyAsync(false);
            Check(failure.IsVisible && !failure.ActionCompleted && failure.EditedText == "保留我的修改", "Copy failure preserves edits");
            failure.Close();
            var delayedCopy = new TaskCompletionSource();
            var closing = new OcrResultWindow(mixedImage!, mixedResult!, (_, _) => delayedCopy.Task);
            closing.Show(); var copyPending = closing.CopyAsync(false); closing.Close(); delayedCopy.SetResult(); await copyPending;
            Check(!closing.ActionCompleted, "Late clipboard callback must not complete closed dialog");
            log.AppendLine("PASS editable result, selected/all Unicode copy, merge/undo, copy error and close during copy (side effects captured)");

            var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new PixelRect(0, 0, mixedImage!.PixelWidth, mixedImage.PixelHeight), mixedImage) });
            foreach (bool finish in new[] { false, true })
            {
                var session = new CaptureSession(snapshot);
                session.CreateOcrResultWindow = (image, result) => new OcrResultWindow(image, result, (text, _) => { copied = text; return Task.CompletedTask; });
                session.Show();
                var owner = Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);
                session.Selection.SetSelection(snapshot.Bounds); session.EndDrag(new(30, 30));
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                var watch = Stopwatch.StartNew(); bool shown = false;
                timer.Tick += async (_, _) =>
                {
                    var popup = Application.Current.Windows.OfType<OcrResultWindow>().FirstOrDefault(w => w.IsVisible);
                    if (popup is not null)
                    {
                        timer.Stop(); shown = true;
                        if (finish) { popup.EditedText = "会话编辑 Session edit"; await popup.CopyAsync(false); }
                        else popup.Close();
                    }
                    else if (watch.Elapsed > TimeSpan.FromSeconds(30)) { timer.Stop(); session.Abort(); }
                };
                timer.Start(); await session.RecognizeTextAsync(owner); timer.Stop();
                Check(shown && !session.Busy && session.IsFinished == finish, "Capture-to-OCR session lifecycle");
                if (finish) Check(copied == "会话编辑 Session edit", "Session copy edited result");
                else { Check(session.Selection.Selection == snapshot.Bounds, "Returning preserves selection"); SavePreview(owner, "ocr-overlay-preview"); }
                session.Abort();
            }
            var late = new TaskCompletionSource<OcrResult>();
            var cancelSession = new CaptureSession(snapshot) { RecognizeText = (_, _) => late.Task };
            var cancelOwner = new OverlayWindow(cancelSession, snapshot.Monitors[0]);
            cancelSession.Selection.SetSelection(snapshot.Bounds);
            var recognition = cancelSession.RecognizeTextAsync(cancelOwner);
            Check(cancelSession.IsRecognizing, "OCR busy state");
            cancelSession.Cancel(); Check(cancelSession.IsFinished, "Esc finishes immediately");
            late.SetResult(mixedResult!); await recognition;
            Check(!cancelSession.Busy && !Application.Current.Windows.OfType<OcrResultWindow>().Any(w => w.IsVisible), "No late OCR popup");
            cancelOwner.Close();
            foreach (bool throwError in new[] { false, true })
            {
                var empty = new CaptureSession(snapshot) { RecognizeText = (_, _) => throwError ? throw new IOException("模型加载失败（测试）") : Task.FromResult(new OcrResult(Array.Empty<OcrLine>())) };
                var owner = new OverlayWindow(empty, snapshot.Monitors[0]);
                empty.Selection.SetSelection(snapshot.Bounds); await empty.RecognizeTextAsync(owner);
                Check(!empty.Busy && !empty.IsFinished && empty.Selection.Selection == snapshot.Bounds && empty.StatusMessage.Length > 0, "Empty/error allows retry and preserves selection");
                empty.Abort(); owner.Close();
            }
            log.AppendLine("PASS real capture/result dialog flow, return to selection, edited copy finishes overlays, no-result/error retry, no late popup after Esc");
            log.AppendLine("ALL OCR CHECKS PASSED");
            File.WriteAllText(Path.Combine(Output, "ocr-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Output, "ocr-test.txt"), log + ex.ToString());
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            return 1;
        }
    }
}
