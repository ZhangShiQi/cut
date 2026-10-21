using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cut.Core;
using Cut.Services;

namespace Cut;

public sealed class CaptureSession
{
    private readonly List<OverlayWindow> windows = new();
    private OverlayWindow? toolbarOwner;
    private bool finished;
    private CancellationTokenSource? recognition;
    private QrResultWindow? resultWindow;
    private readonly Func<IReadOnlyList<QrCodeResult>, QrResultWindow> createResultWindow;
    public bool IsRecognizing { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public bool IsFinished => finished;
    public DesktopSnapshot Snapshot { get; }
    public SelectionController Selection { get; } = new();
    public CaptureInteraction Interaction { get; }
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
        if (Selection.Selection.IsEmpty) Finish();
        else { StatusMessage = ""; Interaction.Reset(NativeMethods.CursorPosition()); Update(); }
    }
    public void ConfirmPreview()
    {
        if (Interaction.ConfirmPreview()) EndDrag(NativeMethods.CursorPosition());
    }
    public void ClearStatus() => StatusMessage = "";
    public void Cancel() { if (!Busy || IsRecognizing) Finish(); }
    public void Abort() => Finish();
    private void Finish()
    {
        if (finished) return;
        finished = true;
        Interaction.LoseCapture();
        recognition?.Cancel();
        resultWindow?.Close();
        foreach (var window in windows) window.Close();
        windows.Clear();
        Completed?.Invoke();
    }
    public async Task RecognizeAsync(OverlayWindow owner)
    {
        if (Busy || Selection.IsDragging || Selection.Selection.IsEmpty || finished) return;
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
            var image = Snapshot.Crop(Selection.Selection);
            var results = await QrCodeService.RecognizeAsync(image, cancellation.Token);
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
    public async Task CopyAsync(OverlayWindow owner)
    {
        if (Busy || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        Update();
        try { await ExportService.CopyAsync(Snapshot.Crop(Selection.Selection)); Finish(); }
        catch (Exception ex) { if (!finished) MessageBox.Show(owner, "无法复制图片，请重试。\n" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; if (!finished) Update(); }
    }
    public void Save(OverlayWindow owner)
    {
        if (Busy || Selection.Selection.IsEmpty || finished) return;
        Busy = true;
        Update();
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
                FileName = "Cut_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", OverwritePrompt = true
            };
            if (dialog.ShowDialog(owner) != true || finished) return;
            ExportService.SavePng(Snapshot.Crop(Selection.Selection), dialog.FileName);
            Finish();
        }
        catch (Exception ex) { if (!finished) MessageBox.Show(owner, "无法保存图片，请选择其他位置重试。\n" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { Busy = false; if (!finished) Update(); }
    }
}
