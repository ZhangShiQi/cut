using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

public sealed class CaptureSession
{
    private readonly List<OverlayWindow> windows = new();
    private OverlayWindow? toolbarOwner;
    private bool finished;
    private CancellationTokenSource? recognition;
    private Window? resultWindow;
    private readonly Func<IReadOnlyList<QrCodeResult>, QrResultWindow> createResultWindow;
    internal Func<System.Windows.Media.Imaging.BitmapSource, OcrResult, OcrResultWindow> CreateOcrResultWindow { get; set; } = (image, result) => new OcrResultWindow(image, result);
    internal Func<System.Windows.Media.Imaging.BitmapSource, CancellationToken, Task<OcrResult>> RecognizeText { get; set; } = OcrService.RecognizeAsync;
    internal Func<System.Windows.Media.Imaging.BitmapSource, VisionResultWindow> CreateVisionResultWindow { get; set; } = image => new(image);
    public bool IsRecognizing { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public bool IsFinished => finished;
    public DesktopSnapshot Snapshot { get; }
    public SelectionController Selection { get; } = new();
    public CaptureInteraction Interaction { get; }
    public AnnotationDocument? Annotations { get; private set; }
    internal AnnotationRenderer? AnnotationRenderer { get; private set; }
    public PixelRect AnnotationBounds { get; private set; }
    public bool AnnotationMode => Annotations is not null && (Annotations.Tool != MarkTool.Select || Annotations.Locked || Annotations.EditingText || Annotations.IsDragging);
    internal Func<BitmapSource, Task> CopyImage { get; set; } = ExportService.CopyAsync;
    internal Action<BitmapSource, string> SaveImage { get; set; } = ExportService.SavePng;
    internal Func<OverlayWindow, string?>? ChooseSavePath { get; set; }
    internal Func<BitmapSource, CancellationToken, Task<IReadOnlyList<QrCodeResult>>> RecognizeCodes { get; set; } = QrCodeService.RecognizeAsync;
    public bool Busy { get; private set; }
    public event Action? Completed;
    public CaptureSession(DesktopSnapshot snapshot, IReadOnlyList<WindowCandidate>? windowTargets = null) : this(snapshot, results => new QrResultWindow(results), windowTargets) { }
    internal CaptureSession(DesktopSnapshot snapshot, Func<IReadOnlyList<QrCodeResult>, QrResultWindow> createResultWindow, IReadOnlyList<WindowCandidate>? windowTargets = null)
    {
        Snapshot = snapshot;
        this.createResultWindow = createResultWindow;
        var targets = windowTargets ?? Array.Empty<WindowCandidate>();
        Interaction = new CaptureInteraction(Selection, snapshot.Bounds, p => WindowDetectionService.FindAt(p, targets, snapshot));
    }

    public void Show()
    {
        var cursor = NativeMethods.CursorPosition();
        foreach (var monitor in Snapshot.Monitors)
        {
            var window = new OverlayWindow(this, monitor);
            windows.Add(window);
            window.Show();
        }
        var active = windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(cursor)) ?? windows[0];
        active.Activate();
        active.Focus();
        Interaction.Hover(cursor);
        Update();
    }
    public void Update() { foreach (var window in windows) window.Refresh(window == toolbarOwner && !Selection.IsDragging && !Selection.Selection.IsEmpty); }
    public void EndDrag(PixelPoint cursor)
    {
        Selection.End();
        toolbarOwner = windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(cursor) && !w.Monitor.Bounds.Intersect(Selection.Selection).IsEmpty)
            ?? windows.FirstOrDefault(w => !w.Monitor.Bounds.Intersect(Selection.Selection).IsEmpty);
        Update();
    }
    public void RightClick()
    {
        if (Busy) return;
        if (AnnotationMode)
        {
            foreach (var window in windows) window.CancelTextEditor();
            Annotations!.CancelDraft(); Annotations.Tool = MarkTool.Select; Update(); return;
        }
        if (Selection.Selection.IsEmpty) Finish();
        else { ResetAnnotations(); StatusMessage = ""; Interaction.Reset(NativeMethods.CursorPosition()); Update(); }
    }
    public void ConfirmPreview()
    {
        if (Interaction.ConfirmPreview()) EndDrag(NativeMethods.CursorPosition());
    }
    public void ClearStatus() => StatusMessage = "";
    internal Func<PixelRect, PixelRect, ScrollCaptureWindow> CreateScrollWindow { get; set; } = (area, monitor) => new(area, monitor);
    internal Func<BitmapSource, LongImageEditor> CreateLongEditor { get; set; } = image => new(image);
    public void StartScroll(OverlayWindow owner)
    {
        if (Busy || finished || Selection.IsDragging || Annotations?.IsDragging == true) return;
        CommitText(); ConfirmPreview(); var area = Selection.Selection;
        var monitor = Snapshot.Monitors.FirstOrDefault(m => area.Intersect(m.Bounds) == area);
        if (area.Width < 128 || area.Height < 160 || monitor is null)
        { StatusMessage = "长截图需在同一显示器内框选至少 128 × 160 像素的内容区。"; Update(); return; }
        if (Annotations?.Locked == true && MessageBox.Show(owner, "长截图需要重新采集实时画面，已有标注不会带入。是否继续？", "Cut", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        Busy = true;
        try
        {
            foreach (var window in windows) window.Hide();
            var capture = CreateScrollWindow(area, monitor.Bounds); resultWindow = capture; capture.ShowDialog(); resultWindow = null;
            if (!finished && capture.Result is BitmapSource image)
            {
                var editor = CreateLongEditor(image); editor.SetUnreliableSeams(capture.Document?.UnreliableAppends ?? 0);
                resultWindow = editor; editor.ShowDialog(); resultWindow = null;
            }
        }
        catch (Exception ex) { if (!finished) MessageBox.Show("长截图失败：" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; Finish(); }
    }
    public void SetTool(MarkTool tool)
    {
        if (Busy || finished) return;
        CommitText(); ConfirmPreview();
        var area = Selection.Selection;
        if (area.IsEmpty) return;
        if ((long)area.Width * area.Height > 32000000) { StatusMessage = "标注最多支持 3200 万像素，请缩小选区。"; Update(); return; }
        if (Annotations is null || AnnotationBounds != area)
        {
            Annotations = new(area.Width, area.Height); AnnotationBounds = area;
            AnnotationRenderer = new(Snapshot.Crop(area));
        }
        if (tool != MarkTool.Select) Annotations.Deselect();
        Annotations.Tool = tool;
        if (tool == MarkTool.Cover) Annotations.Style = Annotations.Style with { Color = Colors.Black };
        StatusMessage = ""; Update();
    }
    public Point AnnotationPoint(PixelPoint p) => new(p.X - AnnotationBounds.X, p.Y - AnnotationBounds.Y);
    public void CommitText() { foreach (var window in windows) window.CommitTextEditor(); }
    public void ChangeMarkStyle(MarkStyle style) { CommitText(); Annotations?.ChangeStyle(style); Update(); }
    public void UndoMark() { CommitText(); Annotations?.Undo(); Update(); }
    public void RedoMark() { CommitText(); Annotations?.Redo(); Update(); }
    public void Reselect(OverlayWindow owner)
    {
        if (Busy) return;
        CommitText();
        if (Annotations?.Locked == true && MessageBox.Show(owner, "重新框选会清空当前标注及撤销历史，是否继续？", "Cut", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        ResetAnnotations(); Interaction.Reset(NativeMethods.CursorPosition()); toolbarOwner = null; StatusMessage = ""; Update();
    }
    internal void ResetAnnotations()
    {
        foreach (var window in windows) window.CancelTextEditor();
        Annotations = null; AnnotationRenderer = null; AnnotationBounds = default;
    }
    public BitmapSource GetOutputImage()
    {
        CommitText();
        if (Annotations?.IsDragging == true) throw new InvalidOperationException("请先完成当前标注。");
        if (Annotations is not null && Annotations.Items.Count > 0)
        {
            if (Selection.Selection != AnnotationBounds) throw new InvalidOperationException("标注选区已变化，请重新框选。");
            return AnnotationRenderer!.Render(Annotations);
        }
        return Snapshot.Crop(Selection.Selection);
    }
    public void Cancel()
    {
        if (Annotations?.EditingText == true || Annotations?.IsDragging == true)
        { foreach (var w in windows) w.CancelTextEditor(); Annotations?.CancelDraft(); foreach (var w in windows) w.ReleaseAnnotationCapture(); Update(); return; }
        if (!Busy || IsRecognizing) Finish();
    }
    public void Abort() => Finish();
    private void Finish()
    {
        if (finished) return;
        finished = true;
        Interaction.LoseCapture();
        recognition?.Cancel();
        foreach (var window in windows) window.CancelTextEditor();
        resultWindow?.Close();
        foreach (var window in windows) window.Close();
        windows.Clear();
        Completed?.Invoke();
    }
    public async Task RecognizeAsync(OverlayWindow owner)
    {
        if (Busy || Selection.IsDragging || Annotations?.IsDragging == true || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        IsRecognizing = true;
        StatusMessage = "正在识别二维码…  Esc 取消";
        using var cancellation = new CancellationTokenSource();
        recognition = cancellation;
        Update();
        try
        {
            if ((long)Selection.Selection.Width * Selection.Selection.Height > QrCodeService.MaxPixels)
                throw new InvalidOperationException("选区过大，请缩小到二维码所在区域后重试。");
            var image = GetOutputImage();
            var results = await RecognizeCodes(image, cancellation.Token);
            if (finished || cancellation.IsCancellationRequested) return;
            IsRecognizing = false;
            if (results.Count == 0)
            {
                StatusMessage = "未找到二维码，请框选完整二维码并保留周围空白。";
                return;
            }
            StatusMessage = "";
            Update();
            var dialog = createResultWindow(results);
            dialog.Owner = owner;
            resultWindow = dialog;
            dialog.ShowDialog();
            resultWindow = null;
            if (dialog.ActionCompleted && !finished) Finish();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!finished) StatusMessage = "识别失败：" + ex.Message; }
        finally
        {
            recognition = null;
            resultWindow = null;
            IsRecognizing = false;
            Busy = false;
            if (!finished) { Update(); owner.Activate(); }
        }
    }
    public async Task RecognizeTextAsync(OverlayWindow owner)
    {
        if (Busy || Selection.IsDragging || Annotations?.IsDragging == true || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        IsRecognizing = true;
        StatusMessage = "正在识别文字… 首次使用需加载模型 · Esc 取消";
        using var cancellation = new CancellationTokenSource();
        recognition = cancellation;
        Update();
        try
        {
            if ((long)Selection.Selection.Width * Selection.Selection.Height > OcrService.MaxPixels)
                throw new InvalidOperationException("选区过大，请缩小到文字所在区域后重试（最多 1600 万像素）。");
            var image = GetOutputImage();
            var result = await RecognizeText(image, cancellation.Token);
            if (finished || cancellation.IsCancellationRequested) return;
            IsRecognizing = false;
            if (result.Lines.Count == 0)
            {
                StatusMessage = "未找到清晰文字，请框选完整文字区域后重试。";
                return;
            }
            StatusMessage = "";
            Update();
            var dialog = CreateOcrResultWindow(image, result);
            dialog.Owner = owner;
            resultWindow = dialog;
            dialog.ShowDialog();
            resultWindow = null;
            if (dialog.ActionCompleted && !finished) Finish();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!finished) StatusMessage = "文字识别失败：" + ex.Message; }
        finally
        {
            recognition = null;
            resultWindow = null;
            IsRecognizing = false;
            Busy = false;
            if (!finished) { Update(); owner.Activate(); }
        }
    }
    public void AnalyzeImage(OverlayWindow owner)
    {
        if (Busy || Selection.IsDragging || Annotations?.IsDragging == true || Selection.Selection.IsEmpty || finished) return;
        Busy = true; StatusMessage = ""; Update();
        try
        {
            if ((long)Selection.Selection.Width * Selection.Selection.Height > VisionImage.MaxPixels)
                throw new AiException("选区超过 3200 万像素，请缩小后分析。");
            var dialog = CreateVisionResultWindow(GetOutputImage());
            dialog.Owner = owner; resultWindow = dialog;
            dialog.ShowDialog(); resultWindow = null;
            if (dialog.ActionCompleted && !finished) Finish();
        }
        catch (AiException ex) { if (!finished) StatusMessage = ex.Message; }
        catch { if (!finished) StatusMessage = "无法打开 AI 看图，请调整选区后重试。"; }
        finally { resultWindow = null; Busy = false; if (!finished) { Update(); owner.Activate(); } }
    }
    public async Task CopyAsync(OverlayWindow owner)
    {
        if (Busy || Annotations?.IsDragging == true || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        Update();
        try { await CopyImage(GetOutputImage()); Finish(); }
        catch (Exception ex) { if (!finished) MessageBox.Show(owner, "无法复制图片，请重试。\n" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; if (!finished) Update(); }
    }
    public void Save(OverlayWindow owner)
    {
        if (Busy || Annotations?.IsDragging == true || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        Update();
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
                FileName = "Cut_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", OverwritePrompt = true
            };
            string? path = ChooseSavePath is null ? (dialog.ShowDialog(owner) == true ? dialog.FileName : null) : ChooseSavePath(owner);
            if (path is null || finished) return;
            SaveImage(GetOutputImage(), path);
            Finish();
        }
        catch (Exception ex) { if (!finished) MessageBox.Show(owner, "无法保存图片，请选择其他位置重试。\n" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; if (!finished) Update(); }
    }
}
