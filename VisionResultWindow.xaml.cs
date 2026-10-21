using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Cut.Services;

namespace Cut;

public partial class VisionResultWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task<VisionImage> prepared;
    private BitmapSource? preview;
    private double zoom = 1;
    private bool fit = true;
    public bool ActionCompleted { get; private set; }
    public VisionResultWindow(BitmapSource image) : this(image, ExportService.CopyTextAsync) { }
    internal VisionResultWindow(BitmapSource image, Func<string, CancellationToken, Task> copy,
        IAiService? service = null, Func<AiSettings>? settings = null)
    {
        InitializeComponent();
        prepared = VisionImage.PrepareAsync(image, lifetime.Token);
        AiPanel.ConfigureVision(token => prepared.WaitAsync(token), copy, service, settings);
        AiPanel.Completed += () => { ActionCompleted = true; Close(); };
        Loaded += async (_, _) =>
        {
            try
            {
                var value = await prepared;
                if (lifetime.IsCancellationRequested) return;
                preview = value.Source; PreviewImage.Source = preview; ApplyZoom();
                ImageInfo.Text = $"原选区 {value.OriginalWidth} × {value.OriginalHeight} → 发送 {preview.PixelWidth} × {preview.PixelHeight} · PNG {value.ByteCount / 1024.0:F0} KB"
                    + (value.Resized ? " · 已等比缩小，细小文字建议重新框选局部。" : " · 保留原始像素。");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (AiException ex) { if (!lifetime.IsCancellationRequested) ImageInfo.Text = ex.Message; }
            catch { if (!lifetime.IsCancellationRequested) ImageInfo.Text = "图片准备失败，请返回重新框选。"; }
        };
        Closed += (_, _) => { lifetime.Cancel(); AiPanel.ClosePanel(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (AiPanel.IsGenerating) AiPanel.Stop("已停止，当前回答已保留；内容可能不完整。");
            else Close();
        };
    }
    private void BackClick(object sender, RoutedEventArgs e) => Close();
    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) { if (fit) ApplyZoom(); }
    private void FitClick(object sender, RoutedEventArgs e) { fit = true; ApplyZoom(); }
    private void ActualClick(object sender, RoutedEventArgs e) { fit = false; zoom = 1; ApplyZoom(); }
    private void ZoomInClick(object sender, RoutedEventArgs e) { fit = false; zoom = Math.Min(4, zoom * 1.25); ApplyZoom(); }
    private void ZoomOutClick(object sender, RoutedEventArgs e) { fit = false; zoom = Math.Max(.05, zoom / 1.25); ApplyZoom(); }
    private void ApplyZoom()
    {
        if (preview is null) return;
        if (fit) zoom = Math.Min(1, Math.Max(.01, Math.Min(Math.Max(1, ImageScroll.ActualWidth - 22) / preview.PixelWidth,
            Math.Max(1, ImageScroll.ActualHeight - 22) / preview.PixelHeight)));
        PreviewImage.Width = preview.PixelWidth * zoom; PreviewImage.Height = preview.PixelHeight * zoom;
        ZoomText.Text = $"{zoom:P0}";
    }
    internal AiAssistantPanel Assistant => AiPanel;
    internal Task<VisionImage> PreparedImage => prepared;
}
