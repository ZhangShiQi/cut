using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal sealed class LongImageEditor : Window
{
    private readonly BitmapSource source;
    private readonly AnnotationRenderer renderer;
    internal AnnotationDocument Document { get; }
    private readonly Canvas canvas = new();
    private readonly EditorSurface surface;
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly WrapPanel toolbar = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10), Foreground = Brushes.DarkSlateGray };
    private readonly TextBlock captureWarning = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 10, 4), Foreground = Brushes.Firebrick, Visibility = Visibility.Collapsed };
    internal void SetUnreliableSeams(int count)
    {
        captureWarning.Text = $"此图含 {count} 处低置信度拼接，可能错位、重复或缺失；保存或复制前请检查接缝。";
        captureWarning.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? recognition;
    private TextBox? text;
    private bool busy, closed, regionMode, regionDragging;
    private System.Windows.Point regionStart;
    internal PixelRect? Region { get; private set; }
    private double zoom = 1;
    private readonly System.Collections.Generic.Dictionary<MarkTool, Button> tools = new();
    private readonly ComboBox widths = new() { Width = 65, Margin = new Thickness(3) }, fonts = new() { Width = 65, Margin = new Thickness(3) };
    private bool syncing;
    internal bool ActionCompleted { get; private set; }
    internal Func<BitmapSource, Task> CopyImage { get; set; } = ExportService.CopyAsync;
    internal Func<string?>? ChooseSavePath { get; set; }
    internal Func<BitmapSource, CancellationToken, Task<OcrResult>> RecognizeText { get; set; } = OcrService.RecognizeAsync;
    internal Func<BitmapSource, VisionResultWindow> CreateVision { get; set; } = image => new(image);
    internal TextBox? TextEditor => text;
    public LongImageEditor(BitmapSource image)
    {
        source = image; renderer = new(image); Document = new(image.PixelWidth, image.PixelHeight);
        Title = "Cut · 长图编辑"; Width = Math.Min(1200, SystemParameters.WorkArea.Width); Height = Math.Min(820, SystemParameters.WorkArea.Height);
        MinWidth = 600; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(241, 245, 249));
        var root = new DockPanel();
        var bar = new ScrollViewer { Content = toolbar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 160, Background = Brushes.White };
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(captureWarning, Dock.Top); root.Children.Add(captureWarning);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        surface = new EditorSurface(this) { Width = image.PixelWidth, Height = image.PixelHeight, Focusable = true };
        canvas.Width = image.PixelWidth; canvas.Height = image.PixelHeight; canvas.Children.Add(surface);
        scroll.Content = canvas; root.Children.Add(scroll); Content = root;
        string[] labels = { "选择", "矩形", "椭圆", "箭头", "画笔", "文字", "马赛克", "实色遮挡" };
        foreach (var tool in Enum.GetValues<MarkTool>()) { var value = tool; tools[tool] = Add(labels[(int)tool], () => SetTool(value)); }
        foreach (var color in new[] { Colors.Red, Colors.Gold, Colors.LimeGreen, Colors.DodgerBlue, Colors.Black, Colors.White })
        {
            var value = color; var button = Add("●", () => { CommitText(); Document.ChangeStyle(Document.Style with { Color = value }); Refresh(); });
            button.Foreground = new SolidColorBrush(value); button.Background = Brushes.DimGray; button.ToolTip = "标注颜色";
        }
        foreach (double value in new[] { 2.0, 4.0, 8.0 }) widths.Items.Add(new ComboBoxItem { Content = value == 2 ? "细" : value == 4 ? "中" : "粗", Tag = value });
        foreach (double value in new[] { 18.0, 28.0, 40.0, 56.0 }) fonts.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });
        widths.SelectedIndex = fonts.SelectedIndex = 1; toolbar.Children.Add(widths); toolbar.Children.Add(fonts);
        widths.SelectionChanged += (_, _) => StyleChanged(); fonts.SelectionChanged += (_, _) => StyleChanged();
        Add("撤销", () => { CommitText(); Document.Undo(); Refresh(); }).ToolTip = "Ctrl + Z";
        Add("重做", () => { CommitText(); Document.Redo(); Refresh(); }).ToolTip = "Ctrl + Y";
        Add("删除", () => { CommitText(); Document.DeleteSelected(); Refresh(); }).ToolTip = "Delete";
        Add("框选局部", () => { CommitText(); regionMode = true; Document.Deselect(); status.Text = "拖动框选输出区域；复制、保存、OCR 和 AI 将使用这部分。"; Refresh(); });
        Add("全图", () => { CommitText(); Region = null; regionMode = false; Refresh(); });
        Add("适应宽度", FitWidth);
        Add("100%", () => SetZoom(1)); Add("－", () => SetZoom(zoom / 1.25)); Add("＋", () => SetZoom(zoom * 1.25));
        Add("识别文字", async () => await OcrAsync());
        Add("AI 看图", Analyze);
        Add("保存 PNG", Save); Add("复制图片", async () => await CopyAsync());
        Add("关闭", Close);
        Loaded += (_, _) => { FitWidth(); surface.Focus(); };
        Closed += (_, _) => { closed = true; recognition?.Cancel(); lifetime.Cancel(); };
        surface.MouseLeftButtonDown += (_, e) => { if (busy) return; BeginAt(e.GetPosition(surface), e.ClickCount); e.Handled = true; };
        surface.MouseMove += (_, e) =>
        {
            if (busy) return;
            if (regionDragging) UpdateRegion(e.GetPosition(surface));
            else Document.Move(e.GetPosition(surface));
            surface.InvalidateVisual();
        };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (regionDragging) { UpdateRegion(e.GetPosition(surface)); regionDragging = false; }
            else Document.End(e.GetPosition(surface));
            surface.ReleaseMouseCapture(); Refresh(); e.Handled = true;
        };
        surface.LostMouseCapture += (_, _) => { Document.CancelDraft(); regionDragging = false; Refresh(); };
        PreviewKeyDown += async (_, e) => e.Handled = await KeyAsync(e.Key, Keyboard.Modifiers);
        Refresh();
    }
    private Button Add(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(3) };
        button.Click += (_, _) => { if (!busy) action(); }; toolbar.Children.Add(button); return button;
    }
    private void StyleChanged()
    {
        if (syncing) return; CommitText();
        Document.ChangeStyle(Document.Style with { Width = (double)((ComboBoxItem)widths.SelectedItem).Tag, FontSize = (double)((ComboBoxItem)fonts.SelectedItem).Tag }); Refresh();
    }
    internal void SetTool(MarkTool tool)
    {
        CommitText(); Document.Deselect(); Document.Tool = tool; regionMode = false;
        if (tool == MarkTool.Cover) Document.Style = Document.Style with { Color = Colors.Black };
        surface.Focus(); Refresh();
    }
    internal void BeginAt(System.Windows.Point p, int clicks = 1)
    {
        CommitText(); surface.Focus();
        if (regionMode) { regionStart = p; regionDragging = true; Region = null; surface.CaptureMouse(); return; }
        int hit = Document.Hit(p, 6 / zoom);
        if (Document.Tool == MarkTool.Text || (Document.Tool == MarkTool.Select && clicks == 2 && hit >= 0 && Document.Items[hit].Tool == MarkTool.Text))
        { BeginText(p, Document.Tool == MarkTool.Select ? hit : -1); return; }
        if (Document.Begin(p, 6 / zoom)) surface.CaptureMouse(); Refresh();
    }
    private void UpdateRegion(System.Windows.Point p)
    {
        int x1 = (int)Math.Clamp(Math.Min(regionStart.X, p.X), 0, source.PixelWidth), y1 = (int)Math.Clamp(Math.Min(regionStart.Y, p.Y), 0, source.PixelHeight);
        int x2 = (int)Math.Clamp(Math.Max(regionStart.X, p.X), 0, source.PixelWidth), y2 = (int)Math.Clamp(Math.Max(regionStart.Y, p.Y), 0, source.PixelHeight);
        Region = x2 > x1 && y2 > y1 ? new(x1, y1, x2 - x1, y2 - y1) : null;
    }
    internal void SelectRegion(PixelRect? region) { Region = region?.Intersect(new(0, 0, source.PixelWidth, source.PixelHeight)); if (Region?.IsEmpty == true) Region = null; Refresh(); }
    internal void BeginText(System.Windows.Point p, int existing = -1)
    {
        CommitText(); Document.BeginText(p, existing); var draft = Document.Draft!;
        text = new TextBox { Text = draft.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000, FontSize = draft.Style.FontSize,
            FontFamily = new FontFamily("Microsoft YaHei UI"), Foreground = new SolidColorBrush(draft.Style.Color),
            Background = draft.Style.Color.R * .299 + draft.Style.Color.G * .587 + draft.Style.Color.B * .114 > 150 ? Brushes.DimGray : Brushes.White,
            Width = Math.Max(20, draft.TextWidth + 6), MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(2) };
        Canvas.SetLeft(text, draft.Start.X); Canvas.SetTop(text, draft.Start.Y); canvas.Children.Add(text);
        text.TextChanged += (_, _) => { if (text is not null) Document.PreviewText(text.Text); surface.InvalidateVisual(); };
        text.Focus(); text.CaretIndex = text.Text.Length; Refresh();
    }
    internal void CommitText()
    {
        if (text is null) return;
        string value = text.Text; canvas.Children.Remove(text); text = null; Document.CommitText(value); Refresh();
    }
    private void CancelText()
    {
        if (text is not null) { canvas.Children.Remove(text); text = null; }
        Document.CancelDraft(); Refresh();
    }
    private void FitWidth() => SetZoom(Math.Min(1, Math.Max(100, scroll.ActualWidth - 25) / source.PixelWidth));
    private void SetZoom(double scale) { CommitText(); zoom = Math.Clamp(scale, .05, 4); canvas.LayoutTransform = new ScaleTransform(zoom, zoom); Refresh(); }
    private void Refresh()
    {
        if (closed) return;
        surface.InvalidateVisual();
        toolbar.IsEnabled = !busy; surface.IsEnabled = !busy;
        foreach (var pair in tools) pair.Value.Background = pair.Key == Document.Tool && !regionMode ? Brushes.PaleTurquoise : Brushes.WhiteSmoke;
        syncing = true;
        foreach (ComboBoxItem item in widths.Items) if ((double)item.Tag == Document.Style.Width) widths.SelectedItem = item;
        foreach (ComboBoxItem item in fonts.Items) if ((double)item.Tag == Document.Style.FontSize) fonts.SelectedItem = item;
        syncing = false;
        if (!busy) status.Text = $"{source.PixelWidth} × {source.PixelHeight} px · {zoom:P0} · " +
            (Region is PixelRect r ? $"局部输出 {r.Width} × {r.Height} · 点击全图可恢复" : regionMode ? "拖动框选局部输出区域" : "全图输出 · 滚轮浏览，选工具标注；文字 Ctrl + Enter 完成");
    }
    internal BitmapSource Output()
    {
        CommitText(); if (Document.IsDragging || regionDragging) throw new InvalidOperationException("请先完成拖动。");
        var image = renderer.Render(Document);
        if (Region is not PixelRect r) return image;
        var crop = new CroppedBitmap(image, new Int32Rect(r.X, r.Y, r.Width, r.Height)); crop.Freeze(); return crop;
    }
    internal async Task CopyAsync()
    {
        if (busy || Document.IsDragging || regionDragging) return;
        CommitText(); busy = true; Refresh();
        try { await CopyImage(Output()); if (!closed) { ActionCompleted = true; Close(); } }
        catch (Exception ex) { if (!closed) status.Text = "复制失败：" + ex.Message; }
        finally { busy = false; toolbar.IsEnabled = true; surface.IsEnabled = true; }
    }
    internal void Save()
    {
        if (busy || Document.IsDragging || regionDragging) return;
        CommitText();
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "保存长截图", Filter = "PNG 图片|*.png", DefaultExt = ".png", AddExtension = true, FileName = "Cut_Long_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png" };
            string? path = ChooseSavePath is null ? (dialog.ShowDialog(this) == true ? dialog.FileName : null) : ChooseSavePath();
            if (path is null) return; ExportService.SavePng(Output(), path); ActionCompleted = true; Close();
        }
        catch (Exception ex) { status.Text = "保存失败：" + ex.Message; }
    }
    internal async Task OcrAsync()
    {
        if (busy || Document.IsDragging || regionDragging) return;
        CommitText(); busy = true; Refresh();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); recognition = cancel;
        try
        {
            int width = Region?.Width ?? source.PixelWidth, height = Region?.Height ?? source.PixelHeight;
            if ((long)width * height > OcrService.MaxPixels) throw new InvalidOperationException("OCR 最多支持 1600 万像素，请先框选局部。");
            status.Text = "正在识别文字… Esc 取消";
            var image = Output(); var result = await RecognizeText(image, cancel.Token);
            if (closed || cancel.IsCancellationRequested) return;
            if (result.Lines.Count == 0) { status.Text = "未找到清晰文字，可框选局部后重试。"; return; }
            var dialog = new OcrResultWindow(image, result) { Owner = this }; dialog.ShowDialog();
            if (dialog.ActionCompleted && !closed) { ActionCompleted = true; Close(); }
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "已取消识别，长图和标注已保留。"; }
        catch (Exception ex) { if (!closed) status.Text = "识别失败：" + ex.Message; }
        finally { recognition = null; busy = false; toolbar.IsEnabled = true; surface.IsEnabled = true; }
    }
    internal void Analyze()
    {
        if (busy || Document.IsDragging || regionDragging) return;
        CommitText(); busy = true; Refresh();
        try
        {
            var dialog = CreateVision(Output()); dialog.Owner = this; dialog.ShowDialog();
            if (dialog.ActionCompleted && !closed) { ActionCompleted = true; Close(); }
        }
        catch (Exception ex) { status.Text = "无法分析：" + ex.Message; }
        finally { busy = false; toolbar.IsEnabled = true; surface.IsEnabled = true; }
    }
    internal async Task<bool> KeyAsync(Key key, ModifierKeys modifiers)
    {
        if (text?.IsKeyboardFocusWithin == true)
        {
            if (key == Key.Escape) { CancelText(); return true; }
            if (key == Key.Enter && modifiers == ModifierKeys.Control) { CommitText(); surface.Focus(); return true; }
            return false;
        }
        if (key == Key.Escape)
        {
            if (recognition is not null) recognition.Cancel();
            else if (Document.IsDragging || regionDragging) { Document.CancelDraft(); regionDragging = false; surface.ReleaseMouseCapture(); Refresh(); }
            else if (!busy) Close();
            return true;
        }
        if (busy || Document.IsDragging || regionDragging) return false;
        if (modifiers == ModifierKeys.Control && key == Key.Z) { Document.Undo(); Refresh(); }
        else if (modifiers == ModifierKeys.Control && key == Key.Y) { Document.Redo(); Refresh(); }
        else if (modifiers == ModifierKeys.None && key == Key.Delete) { Document.DeleteSelected(); Refresh(); }
        else if (modifiers == ModifierKeys.Control && key == Key.C) await CopyAsync();
        else if (modifiers == ModifierKeys.Control && key == Key.S) Save();
        else if (modifiers == ModifierKeys.Control && key == Key.T) await OcrAsync();
        else if (modifiers == ModifierKeys.Control && key == Key.I) Analyze();
        else return false;
        return true;
    }
    private sealed class EditorSurface : FrameworkElement
    {
        private readonly LongImageEditor owner;
        public EditorSurface(LongImageEditor owner) => this.owner = owner;
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawImage(owner.source, new Rect(0, 0, Width, Height));
            owner.renderer.Draw(dc, owner.Document, true, 6 / owner.zoom);
            if (owner.Region is PixelRect r) dc.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2 / owner.zoom) { DashStyle = DashStyles.Dash }, new Rect(r.X, r.Y, r.Width, r.Height));
        }
    }
}
