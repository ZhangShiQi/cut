using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cut.Services;

internal static class StartupHealth
{
    public static async Task CheckAsync(CancellationToken token)
    {
        // Exercise native OpenCV and both actual OCR models, without capturing the desktop
        // or contacting any AI/translation service. Runs before trial startup is committed.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 500, 100));
            dc.DrawText(new FormattedText("Cut OCR 12345", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Arial"), 28, Brushes.Black, 1), new Point(20, 25));
        }
        var bitmap = new RenderTargetBitmap(500, 100, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze();
        await Task.Run(() => OpenCvSharp.Cv2.GetVersionString(), token);
        var result = await OcrService.RecognizeAsync(bitmap, token);
        if (!result.Text.Contains("12345", StringComparison.Ordinal)) throw new InvalidOperationException("OCR 启动自检未通过。");
    }
}
