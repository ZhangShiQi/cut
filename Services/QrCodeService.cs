using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace Cut.Services;

public sealed record QrCodeResult(string Text)
{
    public Uri? WebUri => LinkService.GetWebUri(Text);
    public string Kind => WebUri is null ? "文本" : "网页链接";
    public string Preview
    {
        get
        {
            string line = Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return line.Length <= 100 ? line : line[..100] + "…";
        }
    }
}

public static class QrCodeService
{
    // ZXing's synchronous decode cannot be interrupted midway. Serialize workers
    // across sessions, check cancellation between passes, and discard stale results.
    private static readonly SemaphoreSlim Worker = new(1, 1);
    internal const long MaxPixels = 32_000_000;

    public static async Task<IReadOnlyList<QrCodeResult>> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        if (!image.IsFrozen) throw new ArgumentException("识别图片必须先冻结。", nameof(image));
        if ((long)image.PixelWidth * image.PixelHeight > MaxPixels)
            throw new InvalidOperationException("选区过大，请缩小到二维码所在区域后重试。");
        await Worker.WaitAsync(cancellationToken);
        try { return await Task.Run(() => Decode(image, cancellationToken), cancellationToken); }
        finally { Worker.Release(); }
    }

    private static IReadOnlyList<QrCodeResult> Decode(BitmapSource image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BitmapSource source = image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var bytes = new byte[checked(width * height * 4)];
        source.CopyPixels(bytes, width * 4, 0);
        var results = new List<QrCodeResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Scan(byte[] pixels, int w, int h)
        {
            token.ThrowIfCancellationRequested();
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new DecodingOptions { PossibleFormats = new[] { BarcodeFormat.QR_CODE }, TryHarder = true }
            };
            var decoded = reader.DecodeMultiple(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGR32);
            token.ThrowIfCancellationRequested();
            if (decoded is null) return;
            foreach (var result in decoded)
                if (result.Text is not null && seen.Add(result.Text)) results.Add(new(result.Text));
        }
        Scan(bytes, width, height);
        // Always scan the inverse too: a selection may contain both polarities.
        Invert(bytes, token);
        Scan(bytes, width, height);
        if (results.Count == 0 && width <= 900 && height <= 900)
        {
            // Nearest-neighbor enlargement preserves crisp QR module edges.
            var enlarged = new byte[checked(width * height * 16)];
            for (int y = 0; y < height * 2; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width * 2; x++)
                    Buffer.BlockCopy(bytes, ((y / 2) * width + x / 2) * 4, enlarged, (y * width * 2 + x) * 4, 4);
            }
            Scan(enlarged, width * 2, height * 2);
            Invert(enlarged, token);
            Scan(enlarged, width * 2, height * 2);
        }
        token.ThrowIfCancellationRequested();
        return results;
    }
    private static void Invert(byte[] bytes, CancellationToken token)
    {
        for (int i = 0; i < bytes.Length; i += 4)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            bytes[i] ^= 255; bytes[i + 1] ^= 255; bytes[i + 2] ^= 255;
        }
    }
}
