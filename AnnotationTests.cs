using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal static class AnnotationTests
{
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static byte[] Pixels(BitmapSource source)
    {
        var image = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        byte[] data = new byte[image.PixelWidth * image.PixelHeight * 3];
        image.CopyPixels(data, image.PixelWidth * 3, 0); return data;
    }
    private static Color Pixel(BitmapSource source, int x, int y)
    {
        var data = Pixels(source); int i = (y * source.PixelWidth + x) * 3;
        return Color.FromRgb(data[i + 2], data[i + 1], data[i]);
    }
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < data.Length; i += 4) { data[i] = color.B; data[i + 1] = color.G; data[i + 2] = color.R; }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, data, width * 4); image.Freeze(); return image;
    }
    private static void Mark(AnnotationDocument doc, MarkTool tool, Point a, Point b, Color? color = null)
    {
        doc.Deselect(); doc.Tool = tool; if (color.HasValue) doc.Style = doc.Style with { Color = color.Value };
        doc.Begin(a, 3); doc.End(b);
    }
    private static void Text(AnnotationDocument doc, string value, Point point)
    { doc.Deselect(); doc.BeginText(point); doc.CommitText(value); }
    private static void Same(BitmapSource a, BitmapSource b, string label) =>
        Check(a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight && Pixels(a).SequenceEqual(Pixels(b)), label);
    private static BitmapSource Decode(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze(); return image;
    }
    private static void Preview(Window window, string path)
    {
        window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(window); ExportService.SavePng(image, path);
    }
    private static CaptureSession Session()
    {
        var snapshot = new DesktopSnapshot(new[] { new MonitorSnapshot(new(0, 0, 1000, 720), Solid(1000, 720, Colors.White)) });
        var session = new CaptureSession(snapshot); session.Show();
        session.Selection.SetSelection(new(40, 40, 900, 470)); session.EndDrag(new(50, 50));
        session.SetTool(MarkTool.Cover);
        Mark(session.Annotations!, MarkTool.Cover, new(20, 20), new(130, 80), Colors.Black);
        return session;
    }
    private static OverlayWindow Owner() => Application.Current.Windows.OfType<OverlayWindow>().First(w => w.IsVisible);

    public static async Task<int> RunAsync()
    {
        string output = Path.Combine(Environment.CurrentDirectory, "artifacts"); Directory.CreateDirectory(output);
        var log = new StringBuilder("Cut annotation tests. Synthetic screenshots; no live clipboard/network/config writes.\n");
        try
        {
            var background = Solid(800, 480, Colors.White);
            var doc = new AnnotationDocument(800, 480);
            var renderer = new AnnotationRenderer(background);
            Mark(doc, MarkTool.Rectangle, new(20, 20), new(150, 90), Colors.Red);
            Mark(doc, MarkTool.Ellipse, new(180, 20), new(320, 90), Colors.DodgerBlue);
            Mark(doc, MarkTool.Arrow, new(350, 55), new(500, 55), Colors.LimeGreen);
            doc.Deselect(); doc.Tool = MarkTool.Pen; doc.Style = new(Colors.Red, 8);
            doc.Begin(new(30, 140), 3); doc.Move(new(70, 170)); doc.End(new(120, 130));
            Mark(doc, MarkTool.Pen, new(150, 150), new(150, 150), Colors.Red);
            doc.Style = new(Colors.Black, 4, 28); Text(doc, "中英标注 Hello 2026\n第二行", new(220, 120));
            Mark(doc, MarkTool.Cover, new(30, 240), new(200, 310), Colors.Black);
            Mark(doc, MarkTool.Mosaic, new(60, 265), new(170, 280));
            var result = renderer.Render(doc);
            Check(Pixel(result, 20, 50) == Colors.Red && Pixel(result, 80, 50) == Colors.White, "Rectangle outline/hollow");
            Check(Pixel(result, 250, 20).B > 200 && Pixel(result, 250, 55) == Colors.White, "Ellipse outline/hollow");
            Check(Pixel(result, 490, 56).G > 100 && Pixel(result, 490, 56).R < 100, "Arrow head");
            Check(Pixel(result, 70, 170) == Colors.Red && doc.Hit(new(150, 150), 3) == 4, "Pen stroke and dot hit");
            Check(Pixel(result, 100, 270) == Colors.Black, "Mosaic never reveals covered source");
            Check(doc.Hit(new(100, 270), 3) == 6, "Mosaic is not movable/selectable");
            Check(Pixels(result).Where((_, i) => i >= (120 * 800 + 220) * 3 && i < (210 * 800 + 600) * 3).Count(b => b == 0) > 100, "Text rasterized");
            string png = Path.Combine(output, "annotation-output.png"); ExportService.SavePng(result, png);
            Same(result, Decode(File.ReadAllBytes(png)), "PNG exact roundtrip");
            doc.Tool = MarkTool.Select;
            Same(result, renderer.Render(doc), "Selection controls excluded from output");
            log.AppendLine("PASS seven tools, hollow shapes, arrowhead, pen dot, CJK multiline text, opaque cover, mosaic ordering, PNG");
            foreach (double width in new[] { 2.0, 4.0, 8.0 })
            {
                var arrows = new AnnotationDocument(160, 160); arrows.Style = new(Colors.Red, width);
                Mark(arrows, MarkTool.Arrow, new(20, 30), new(120, 30));
                Mark(arrows, MarkTool.Arrow, new(120, 70), new(20, 70));
                Mark(arrows, MarkTool.Arrow, new(90, 90), new(90, 140));
                var sharp = new AnnotationRenderer(Solid(160, 160, Colors.White)).Render(arrows);
                Check(Pixel(sharp, 121, 30) == Colors.White && Pixel(sharp, 18, 70) == Colors.White && Pixel(sharp, 90, 141) == Colors.White, "Arrow shafts never protrude beyond tip at width " + width);
                Check(Pixel(sharp, 119, 32) == Colors.White && Pixel(sharp, 116, 30).R == 255 && Pixel(sharp, 116, 30).G < 180, "Narrow triangular tip at width " + width);
            }
            log.AppendLine("PASS sharp arrow tips at all three widths and both horizontal/vertical directions; no round shaft protrusion");

            var edit = new AnnotationDocument(400, 300);
            Mark(edit, MarkTool.Rectangle, new(20, 20), new(100, 100));
            edit.Tool = MarkTool.Select; edit.Begin(new(60, 20), 3); edit.End(new(80, 40));
            Check(edit.Items[0].Start == new Point(40, 40), "Move rectangle");
            edit.Begin(new(120, 120), 3); edit.End(new(150, 160));
            Check(AnnotationDrawing.Bounds(edit.Items[0]).BottomRight == new Point(150, 160), "Resize rectangle");
            edit.ChangeStyle(new(Colors.Blue, 8)); Check(edit.Items[0].Style.Width == 8, "Change selected style");
            edit.Undo(); Check(edit.Items[0].Style.Width == 4, "Undo style"); edit.Redo();
            edit.DeleteSelected(); // Undo/redo deselects; reselect before delete.
            edit.Tool = MarkTool.Select; edit.Begin(new(80, 40), 3); edit.End(new(80, 40)); edit.DeleteSelected();
            Check(edit.Items.Count == 0, "Delete selected"); edit.Undo(); Check(edit.Items.Count == 1, "Restore deleted");
            edit.Undo(); Mark(edit, MarkTool.Arrow, new(180, 40), new(250, 40));
            Check(!edit.CanRedo, "New edit clears redo");
            edit.Tool = MarkTool.Select; edit.Begin(new(250, 40), 3); edit.End(new(280, 80));
            Check(edit.Items[^1].End == new Point(280, 80), "Arrow endpoint");
            var before = edit.Items.ToArray(); edit.Begin(new(280, 80), 3); edit.Move(new(300, 100)); edit.CancelDraft();
            Check(edit.Items.SequenceEqual(before), "Canceled drag leaves committed objects");
            edit.Style = new(Colors.Black); Text(edit, "original", new(10, 200)); int ti = edit.Items.Count - 1;
            edit.BeginText(new(), ti); edit.CommitText("编辑后的文字");
            edit.Undo(); Check(edit.Items[^1].Text == "original", "Undo text edit"); edit.Redo();
            edit.BeginText(new(), ti); edit.CommitText(""); Check(edit.Items.Count == ti, "Empty edited text deletes"); edit.Undo();
            var history = new AnnotationDocument(50, 50);
            for (int i = 0; i < 105; i++) Mark(history, MarkTool.Pen, new(10, 10), new(10, 10));
            int undos = 0; while (history.CanUndo) { history.Undo(); undos++; }
            Check(undos == 100 && history.Items.Count == 5 && history.Locked, "Bounded history and sticky selection lock");
            log.AppendLine("PASS move, resize, arrow endpoints, style, delete, text edit, cancel, redo invalidation, 100-edit history");

            // Nonuniform source makes pixelation observable, including the last partial block.
            byte[] noise = new byte[67 * 47 * 4]; new Random(123).NextBytes(noise);
            var noiseImage = BitmapSource.Create(67, 47, 96, 96, PixelFormats.Bgr32, null, noise, 67 * 4); noiseImage.Freeze();
            var mosaicDoc = new AnnotationDocument(67, 47); mosaicDoc.Style = new(Colors.Black, 32);
            Mark(mosaicDoc, MarkTool.Mosaic, new(33, 23), new(33, 23));
            var mosaicRenderer = new AnnotationRenderer(noiseImage); var mosaic = mosaicRenderer.Render(mosaicDoc);
            Check(Pixel(mosaic, 3, 3) == Pixel(mosaic, 12, 12) && Pixel(mosaic, 64, 46) == Pixel(mosaic, 66, 46), "16-pixel mosaic blocks and partial edge");
            Check(!Pixels(mosaic).SequenceEqual(Pixels(noiseImage)), "Mosaic changes pixels");
            Mark(mosaicDoc, MarkTool.Cover, new(0, 0), new(67, 47), Colors.Black);
            Mark(mosaicDoc, MarkTool.Mosaic, new(33, 23), new(33, 23));
            Check(Pixels(mosaicRenderer.Render(mosaicDoc)).All(b => b == 0), "Mosaic after opaque cover remains opaque");
            mosaicDoc.Undo(); mosaicDoc.Undo();
            Same(mosaic, mosaicRenderer.Render(mosaicDoc), "Renderer cache invalidation after undo");

            var desktop = new DesktopSnapshot(new[] {
                new MonitorSnapshot(new(-400, -100, 400, 300), Solid(400, 300, Colors.Red)),
                new MonitorSnapshot(new(0, 0, 400, 300), Solid(400, 300, Colors.Blue)) });
            var cross = new CaptureSession(desktop);
            cross.Selection.SetSelection(new(-100, -50, 300, 200)); cross.SetTool(MarkTool.Cover);
            Mark(cross.Annotations!, MarkTool.Cover, new(90, 40), new(110, 70), Colors.Black);
            var crossed = cross.GetOutputImage();
            Check(Pixel(crossed, 20, 20) == Colors.Red && Pixel(crossed, 200, 100) == Colors.Blue && Pixel(crossed, 200, 20) == Colors.Black && Pixel(crossed, 100, 50) == Colors.Black, "Negative origin, cross monitor, gap, local annotation coordinates");
            Check(cross.AnnotationPoint(new(-10, -10)) == new Point(90, 40), "Physical desktop to local pixels");
            foreach (double scale in new[] { 1.0, 1.5, 2.0 })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen()) { dc.PushTransform(new ScaleTransform(scale, scale)); cross.AnnotationRenderer!.Draw(dc, cross.Annotations!); dc.Pop(); }
                var scaled = new RenderTargetBitmap((int)(300 * scale), (int)(200 * scale), 96, 96, PixelFormats.Pbgra32); scaled.Render(visual);
                Check(Pixel(scaled, (int)(100 * scale), (int)(50 * scale)) == Colors.Black && Pixel(scaled, (int)(20 * scale), (int)(20 * scale)) == Colors.Red, "Scaled preview matches physical coordinates");
            }
            cross.Abort();
            log.AppendLine("PASS mosaic blocks, ordered redaction, cache invalidation, negative origins/cross-monitor gaps and 100/150/200% transform checks");

            var ui = Session(); var owner = Owner(); var uiDoc = ui.Annotations!;
            ui.SetTool(MarkTool.Rectangle); owner.BeginAnnotation(new(210, 90), 2); uiDoc.End(new(330, 130)); owner.ReleaseAnnotationCapture();
            Check(!ui.IsFinished && uiDoc.Items.Count == 2, "Double click in annotation mode does not copy/exit");
            var selection = ui.Selection.Selection;
            ui.SetTool(MarkTool.Select); owner.BeginAnnotation(new(500, 400)); owner.ReleaseAnnotationCapture();
            Check(ui.Selection.Selection == selection, "Empty click leaves locked screenshot selection");
            ui.SetTool(MarkTool.Text); owner.StartTextEditor(new(200, 180));
            owner.ActiveTextEditor!.Text = "标注文字 Hello\n可编辑";
            Check(!await owner.HandleKeyAsync(Key.Enter, ModifierKeys.None), "Text Enter remains newline");
            await owner.HandleKeyAsync(Key.Enter, ModifierKeys.Control);
            Check(owner.ActiveTextEditor is null && uiDoc.Items[^1].Text.Contains("Hello"), "Ctrl Enter commits text without copying");
            ui.SetTool(MarkTool.Select); owner.BeginAnnotation(new(245, 225), 2);
            Check(owner.ActiveTextEditor is not null, "Double click text enters editor");
            owner.ActiveTextEditor!.Text = "discard me"; await owner.HandleKeyAsync(Key.Escape, ModifierKeys.None);
            Check(uiDoc.Items[^1].Text.Contains("Hello") && !ui.IsFinished, "Escape text rolls back without exiting");
            await owner.HandleKeyAsync(Key.Z, ModifierKeys.Control); Check(uiDoc.Items.Count == 2, "UI undo shortcut");
            await owner.HandleKeyAsync(Key.Y, ModifierKeys.Control); Check(uiDoc.Items.Count == 3, "UI redo shortcut");
            ui.SetTool(MarkTool.Pen); owner.BeginAnnotation(new(500, 300)); uiDoc.Move(new(500, 330));
            await owner.HandleKeyAsync(Key.Escape, ModifierKeys.None);
            Check(!uiDoc.IsDragging && Mouse.Captured is null && !ui.IsFinished, "Escape stroke releases capture and preserves session");
            ui.SetTool(MarkTool.Ellipse); Mark(uiDoc, MarkTool.Ellipse, new(450, 40), new(600, 130), Colors.DodgerBlue);
            Mark(uiDoc, MarkTool.Arrow, new(450, 210), new(600, 160), Colors.Red);
            Mark(uiDoc, MarkTool.Pen, new(30, 340), new(220, 380), Colors.LimeGreen);
            Mark(uiDoc, MarkTool.Mosaic, new(40, 45), new(110, 45));
            ui.SetTool(MarkTool.Select); ui.Update(); await Task.Delay(80);
            Preview(owner, Path.Combine(output, "annotation-ui.png"));
            owner.Width = 560; owner.Height = 620; owner.UpdateLayout(); owner.Refresh(true);
            Preview(owner, Path.Combine(output, "annotation-ui-compact.png"));
            owner.Width = 1000; owner.Height = 720; owner.UpdateLayout();

            BitmapSource expected = ui.GetOutputImage(); BitmapSource? seenOcr = null, seenQr = null, seenVision = null;
            ui.RecognizeText = (image, _) => { seenOcr = image; return Task.FromResult(new OcrResult(Array.Empty<OcrLine>())); };
            await ui.RecognizeTextAsync(owner); Same(expected, seenOcr!, "OCR consumer gets composite");
            BitmapSource? seenPreview = null, seenFromOcr = null;
            ui.RecognizeText = (image, _) => Task.FromResult(new OcrResult(new[] { new OcrLine("synthetic", new(0, 0, 20, 20), 1) }));
            ui.CreateOcrResultWindow = (image, result) =>
            {
                var dialog = new OcrResultWindow(image, result);
                dialog.CreateVisionWindow = picture =>
                {
                    seenFromOcr = picture;
                    var vision = new VisionResultWindow(picture); vision.Loaded += (_, _) => vision.Dispatcher.BeginInvoke(new Action(vision.Close)); return vision;
                };
                dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
                { seenPreview = (BitmapSource)dialog.PreviewImage.Source; dialog.OpenVision(); dialog.Close(); }));
                return dialog;
            };
            await ui.RecognizeTextAsync(owner);
            Same(expected, seenPreview!, "OCR result preview uses composite");
            Same(expected, seenFromOcr!, "OCR to vision uses composite");
            ui.RecognizeCodes = (image, _) => { seenQr = image; return Task.FromResult<System.Collections.Generic.IReadOnlyList<QrCodeResult>>(Array.Empty<QrCodeResult>()); };
            await ui.RecognizeAsync(owner); Same(expected, seenQr!, "QR consumer gets composite");
            ui.CreateVisionResultWindow = image => { seenVision = image; var dialog = new VisionResultWindow(image); dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(dialog.Close)); return dialog; };
            await owner.HandleKeyAsync(Key.I, ModifierKeys.Control);
            Same(expected, seenVision!, "Ctrl I consumer gets composite");
            Check(!ui.IsFinished && !ui.Busy && uiDoc.Items.Count == 7, "Return from recognition retains all annotations");
            var encoded = await VisionImage.PrepareAsync(seenVision!);
            Same(expected, Decode(Convert.FromBase64String(encoded.DataUrl.Split(',', 2)[1])), "Actual vision payload contains composed PNG");
            ui.Abort();

            // Right click leaves drawing without losing marks, then the same pointer
            // handler used by real mouse input must copy the composed image on double click.
            var exited = Session(); var exitedOwner = Owner(); var kept = exited.GetOutputImage();
            exited.SetTool(MarkTool.Pen); exitedOwner.BeginAnnotation(new(300, 200)); exited.Annotations!.Move(new(350, 220));
            exited.RightClick();
            Check(!exited.AnnotationMode && !exited.Annotations.IsDragging && Mouse.Captured is null && exited.Annotations.Items.Count == 1, "Right click cancels stroke, exits mode and releases capture");
            Same(kept, exited.GetOutputImage(), "Right click preserves committed annotations");
            var lockedBounds = exited.Selection.Selection;
            await exitedOwner.HandlePointerDownAsync(new(300, 200), 1);
            Check(exited.Selection.Selection == lockedBounds && !exited.Interaction.IsPointerDown, "Exited mode cannot move a marked selection");
            exited.SetTool(MarkTool.Text); exitedOwner.StartTextEditor(new(200, 180)); exitedOwner.ActiveTextEditor!.Text = "Cancel draft";
            exited.RightClick();
            Check(!exited.AnnotationMode && exitedOwner.ActiveTextEditor is null && exited.Annotations.Items.Count == 1, "Right click cancels text editor and exits mode");
            BitmapSource? doubleClickCopy = null;
            exited.CopyImage = image => { doubleClickCopy = image; return Task.CompletedTask; };
            await exitedOwner.HandlePointerDownAsync(new(300, 200), 2);
            Check(exited.IsFinished && doubleClickCopy is not null, "Double click after right click copies and ends screenshot");
            Same(kept, doubleClickCopy!, "Double click copies all committed marks");
            var reentered = Session(); var reenteredOwner = Owner();
            reentered.RightClick(); reentered.SetTool(MarkTool.Rectangle);
            await reenteredOwner.HandlePointerDownAsync(new(400, 300), 2);
            Check(reentered.AnnotationMode && !reentered.IsFinished && reentered.Annotations!.IsDragging, "Selecting a tool re-enters drawing and guards double-click copy");
            reentered.RightClick(); reentered.RightClick();
            Check(reentered.Annotations is null && !reentered.AnnotationMode && reentered.Selection.Selection.IsEmpty, "Second right click restores clear-selection behavior");
            reentered.Abort();
            log.AppendLine("PASS right-click exits drawing/text draft without losing marks, locked bounds, double-click composed copy, tool re-entry and second-right-click reset");

            static System.Collections.Generic.IEnumerable<Button> AllButtons(DependencyObject parent)
            {
                if (parent is Button button) yield return button;
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                    foreach (var child in AllButtons(VisualTreeHelper.GetChild(parent, i))) yield return child;
            }
            var vector = Session(); var vectorOwner = Owner(); vectorOwner.UpdateLayout();
            var vectorButtons = AllButtons(vectorOwner).ToArray();
            Check(vectorButtons.Length >= 24, "All screenshot actions and palette buttons found");
            foreach (var button in vectorButtons)
            {
                Check(button.Width == 36 && button.Height == 36 && button.Content is Image { Source: DrawingImage }, "Uniform SVG-backed vector button");
                Check(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && button.ToolTip is not null, "Accessible name and tooltip preserved");
            }
            foreach (var key in new[] { "矩形", "椭圆", "箭头", "画笔", "文字", "马赛克", "实色遮挡" })
                vectorButtons.Single(b => AutomationProperties.GetName(b) == key).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(vector.AnnotationMode && vector.Annotations!.Tool == MarkTool.Cover, "SVG tool buttons retain click actions");
            Preview(vectorOwner, Path.Combine(output, "annotation-svg-toolbar.png"));
            vectorOwner.Width = 360; vectorOwner.Height = 560; vectorOwner.UpdateLayout(); vectorOwner.Refresh(true);
            Preview(vectorOwner, Path.Combine(output, "annotation-svg-toolbar-compact.png"));
            vector.Abort();
            log.AppendLine("PASS SVG resources render, uniform 36px buttons, accessible tooltips/names, real button actions and narrow layout");

            var copy = Session(); BitmapSource? copied = null; copy.CopyImage = image => { copied = image; return Task.CompletedTask; };
            var copyOwner = Owner(); copy.SetTool(MarkTool.Text); copyOwner.StartTextEditor(new(200, 150)); copyOwner.ActiveTextEditor!.Text = "Copy pending text";
            await copy.CopyAsync(copyOwner);
            Check(copy.IsFinished && copy.Annotations!.Items.Count == 2 && Pixel(copied!, 40, 40) == Colors.Black, "Copy commits text and exports composite");
            var save = Session(); var saveOwner = Owner();
            save.ChooseSavePath = _ => null; save.Save(saveOwner); Check(!save.IsFinished && save.Annotations!.Items.Count == 1, "Cancel save preserves annotation/history");
            string saved = Path.Combine(output, "annotation-saved.png");
            save.ChooseSavePath = _ => saved; var saveExpected = save.GetOutputImage(); save.Save(saveOwner);
            Check(save.IsFinished, "Save finishes"); Same(saveExpected, Decode(File.ReadAllBytes(saved)), "Saved PNG has composite pixels");
            var close = Session(); var closeOwner = Owner(); close.SetTool(MarkTool.Text); closeOwner.StartTextEditor(new(50, 150)); closeOwner.ActiveTextEditor!.Text = "Uncommitted";
            closeOwner.Close(); await Task.Delay(30); Check(close.IsFinished, "Closing overlay aborts even during text editing");
            log.AppendLine("PASS UI text editor/keyboard, double-click guard, capture cancellation, locked selection, compact layout, copy/save/OCR/QR/AI unified output and PNG payload");

            var ocrBase = OcrTests.Render(new[] { "SECRET 543210", "Visible example" }, 28, false, 700);
            var ocrDoc = new AnnotationDocument(ocrBase.PixelWidth, ocrBase.PixelHeight);
            Mark(ocrDoc, MarkTool.Cover, new(0, 0), new(700, 70), Colors.White);
            ocrDoc.Style = new(Colors.Black, 4, 28); Text(ocrDoc, "新增文字 Hello", new(28, 20));
            var ocrImage = new AnnotationRenderer(ocrBase).Render(ocrDoc);
            var recognized = await OcrService.RecognizeAsync(ocrImage, CancellationToken.None);
            Check(recognized.Text.Contains("Hello") && recognized.Text.Contains("新增文字") && !recognized.Text.Contains("543210") && !recognized.Text.Contains("SECRET"), "Real OCR sees added text and not covered text: " + recognized.Text);
            log.AppendLine("PASS real local OCR recognizes added CJK/English text and cannot see covered source");
            log.AppendLine("ALL ANNOTATION CHECKS PASSED");
            File.WriteAllText(Path.Combine(output, "annotation-test.txt"), log.ToString()); return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "annotation-test.txt"), log + ex.ToString());
            foreach (var overlay in Application.Current.Windows.OfType<OverlayWindow>().ToArray()) overlay.Close();
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            return 1;
        }
    }
}
