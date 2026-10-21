using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cut.Services;

// No public raw-data constructor: every request image has passed the same bounded encoder.
public sealed class VisionImage
{
    public const long MaxPixels = 32000000;
    public const int MaxDimension = 4096, MaxBytes = 4 * 1024 * 1024;
    public BitmapSource Source { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public int ByteCount { get; }
    internal string DataUrl { get; }
    public bool Resized => Source.PixelWidth != OriginalWidth || Source.PixelHeight != OriginalHeight;
    private VisionImage(BitmapSource source, int width, int height, byte[] png)
    {
        Source = source; OriginalWidth = width; OriginalHeight = height; ByteCount = png.Length;
        DataUrl = "data:image/png;base64," + Convert.ToBase64String(png);
    }
    public static Task<VisionImage> PrepareAsync(BitmapSource source, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if ((long)source.PixelWidth * source.PixelHeight > MaxPixels)
            throw new AiException("选区超过 3200 万像素，请缩小选区后分析。");
        var frozen = source.IsFrozen ? source : source.CloneCurrentValue(); frozen.Freeze();
        return Task.Run(() =>
        {
            double scale = Math.Min(1, (double)MaxDimension / Math.Max(frozen.PixelWidth, frozen.PixelHeight));
            for (int attempt = 0; attempt < 12; attempt++, scale *= .75)
            {
                token.ThrowIfCancellationRequested();
                BitmapSource image = frozen;
                if (scale < 1)
                {
                    int width = Math.Max(1, (int)Math.Floor(frozen.PixelWidth * scale));
                    int height = Math.Max(1, (int)Math.Floor(frozen.PixelHeight * scale));
                    var resized = new TransformedBitmap(frozen, new ScaleTransform((double)width / frozen.PixelWidth, (double)height / frozen.PixelHeight));
                    resized.Freeze(); image = resized;
                }
                byte[] png = ExportService.EncodePng(image);
                token.ThrowIfCancellationRequested();
                if (png.Length <= MaxBytes) return new VisionImage(image, frozen.PixelWidth, frozen.PixelHeight, png);
                if (image.PixelWidth < 64 || image.PixelHeight < 64) break;
            }
            throw new AiException("截图压缩后仍过大，请缩小选区后重试。");
        }, token);
    }
    internal static (BitmapSource Image, string Code) CreateProbe()
    {
        string code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 360, 120));
            dc.DrawEllipse(Brushes.Teal, null, new Point(35, 60), 18, 18);
            dc.DrawText(new FormattedText(code, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 52, Brushes.Black, 1), new Point(76, 24));
        }
        var image = new RenderTargetBitmap(360, 120, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze();
        return (image, code);
    }
}
