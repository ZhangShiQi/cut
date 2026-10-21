using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cut.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Cut.Services;

public sealed record OcrLine(string Text, PixelRect Bounds, float Confidence);
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines.Select(line => line.Text));
}

public interface IOcrEngine
{
    OcrResult Recognize(BitmapSource image, CancellationToken cancellationToken);
}

public static class OcrService
{
    public const long MaxPixels = 16_000_000;
    private static readonly SemaphoreSlim Worker = new(1, 1);
    private static PaddleOcrEngine? engine;

    public static async Task<OcrResult> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken)
    {
        if (!image.IsFrozen) throw new ArgumentException("识别图片必须先冻结。", nameof(image));
        if ((long)image.PixelWidth * image.PixelHeight > MaxPixels)
            throw new InvalidOperationException("选区过大，请缩小到文字所在区域后重试（最多 1600 万像素）。");
        await Worker.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                engine ??= new PaddleOcrEngine(Path.Combine(AppContext.BaseDirectory, "Models"));
                cancellationToken.ThrowIfCancellationRequested();
                return engine.Recognize(image, cancellationToken);
            }, cancellationToken);
        }
        finally { Worker.Release(); }
    }
}

// CPU-only, local inference. Models and character dictionary are versioned together.
internal sealed class PaddleOcrEngine : IOcrEngine, IDisposable
{
    private readonly InferenceSession detector;
    private readonly InferenceSession recognizer;
    private readonly string[] characters;
    private const int TileSize = 1536;
    private const int TileOverlap = 192;

    public PaddleOcrEngine(string directory)
    {
        string det = VerifyModel(directory, "ch_PP-OCRv5_det_mobile.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae");
        string rec = VerifyModel(directory, "ch_PP-OCRv5_rec_mobile.onnx", "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5");
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false
        };
        detector = new InferenceSession(det, options);
        InferenceSession? pending = null;
        try
        {
            pending = new InferenceSession(rec, options);
            if (!pending.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? dictionary))
                throw new InvalidDataException("OCR 模型缺少字符字典，请重新解压完整发布包。");
            characters = new[] { "" }.Concat(dictionary.TrimEnd('\r', '\n').Split('\n').Select(c => c.TrimEnd('\r'))).Append(" ").ToArray();
            recognizer = pending;
        }
        catch { pending?.Dispose(); detector.Dispose(); throw; }
    }

    internal static string VerifyModel(string directory, string name, string expectedHash)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) throw new FileNotFoundException("缺少离线 OCR 模型，请重新解压完整发布包（包含 Models 文件夹）。", name);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("OCR 模型校验失败，请重新解压完整发布包：" + name);
        return path;
    }

    public OcrResult Recognize(BitmapSource image, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = image.Format == PixelFormats.Bgr24 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        var pixels = new byte[checked(source.PixelWidth * source.PixelHeight * 3)];
        source.CopyPixels(pixels, source.PixelWidth * 3, 0);
        using var mat = new Mat(source.PixelHeight, source.PixelWidth, MatType.CV_8UC3);
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        var regions = new List<Region>();
        foreach (int y in TileStarts(mat.Height))
        foreach (int x in TileStarts(mat.Width))
        {
            token.ThrowIfCancellationRequested();
            using var tile = new Mat(mat, new Rect(x, y, Math.Min(TileSize, mat.Width - x), Math.Min(TileSize, mat.Height - y)));
            regions.AddRange(Detect(tile, x, y, token));
            if (regions.Count > 1200) throw new InvalidOperationException("文字区域过多，请分块框选后识别。");
        }
        // Overlapping tiles must not repeat a line. Prefer the more complete box.
        var unique = new List<Region>();
        foreach (var region in regions.OrderByDescending(r => r.Bounds.Width * r.Bounds.Height))
        {
            if (unique.Any(r => Overlap(r.Bounds, region.Bounds) > .65)) continue;
            unique.Add(region);
        }
        // A horizontal line crossing a tile seam has two partial boxes. Join the
        // overlapping fragments in source coordinates before recognizing it once.
        for (int i = 0; i < unique.Count; i++)
        for (int j = i + 1; j < unique.Count; j++)
        {
            var a = unique[i]; var b = unique[j];
            if (a.TileX == b.TileX || !IsHorizontal(a) || !IsHorizontal(b)) continue;
            var overlap = a.Bounds.Intersect(b.Bounds);
            int minHeight = Math.Min(a.Bounds.Height, b.Bounds.Height);
            if (overlap.Width < 10 || overlap.Height < minHeight * .7 ||
                Math.Abs(a.Bounds.Y + a.Bounds.Height / 2.0 - b.Bounds.Y - b.Bounds.Height / 2.0) > minHeight * .3) continue;
            int left = Math.Min(a.Bounds.X, b.Bounds.X), top = Math.Min(a.Bounds.Y, b.Bounds.Y);
            int right = Math.Max(a.Bounds.Right, b.Bounds.Right), bottom = Math.Max(a.Bounds.Bottom, b.Bounds.Bottom);
            unique[i] = new(new Point2f[] { new(left, top), new(right, top), new(right, bottom), new(left, bottom) }, new(left, top, right - left, bottom - top), -1);
            unique.RemoveAt(j);
            j = i; // A third tile may extend the newly joined line.
        }
        if (unique.Count > 500) throw new InvalidOperationException("文字区域过多，请分块框选后识别（最多 500 个文本区域）。");
        var lines = new List<OcrLine>();
        foreach (var region in unique)
        {
            token.ThrowIfCancellationRequested();
            using var crop = CropLine(mat, region.Points);
            var (text, score) = ReadLine(crop, token);
            if (score >= .5f && !string.IsNullOrWhiteSpace(text))
                lines.Add(new(text.Trim(), region.Bounds, score));
        }
        token.ThrowIfCancellationRequested();
        return new OcrResult(OrderLines(lines));
    }

    internal static IEnumerable<int> TileStarts(int length)
    {
        int start = 0;
        while (true)
        {
            yield return start;
            if (start + TileSize >= length) yield break;
            start = Math.Min(start + TileSize - TileOverlap, length - TileSize);
        }
    }

    private sealed record Region(Point2f[] Points, PixelRect Bounds, int TileX);
    private static bool IsHorizontal(Region region)
    {
        var sorted = region.Points.OrderBy(p => p.X).ToArray();
        float leftY = (sorted[0].Y + sorted[1].Y) / 2, rightY = (sorted[2].Y + sorted[3].Y) / 2;
        return Math.Abs(leftY - rightY) <= region.Bounds.Width * .08;
    }
    private static double Overlap(PixelRect a, PixelRect b)
    {
        var intersection = a.Intersect(b);
        return (double)intersection.Width * intersection.Height / Math.Max(1, Math.Min((long)a.Width * a.Height, (long)b.Width * b.Height));
    }

    private List<Region> Detect(Mat image, int offsetX, int offsetY, CancellationToken token)
    {
        // Padding helps detect text touching the selected image's edge. Keep original
        // pixels on large screenshots; tile instead of shrinking small characters away.
        const int border = 24;
        using var padded = new Mat();
        Cv2.CopyMakeBorder(image, padded, border, border, border, border, BorderTypes.Replicate);
        double scale = Math.Min(2, Math.Max(1, 640.0 / Math.Max(padded.Width, padded.Height)));
        int width = Math.Max(32, (int)Math.Ceiling(padded.Width * scale / 32) * 32);
        int height = Math.Max(32, (int)Math.Ceiling(padded.Height * scale / 32) * 32);
        using var resized = new Mat();
        Cv2.Resize(padded, resized, new Size(width, height));
        var tensor = ToTensor(resized, width, true);
        using var output = Run(detector, tensor, token);
        var map = output.First().AsTensor<float>();
        int mapH = map.Dimensions[^2], mapW = map.Dimensions[^1];
        float[] probabilities = map.ToArray();
        byte[] threshold = new byte[probabilities.Length];
        for (int i = 0; i < threshold.Length; i++) threshold[i] = probabilities[i] >= .3f ? (byte)255 : (byte)0;
        using var mask = new Mat(mapH, mapW, MatType.CV_8UC1);
        Marshal.Copy(threshold, 0, mask.Data, threshold.Length);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var result = new List<Region>();
        foreach (var contour in contours)
        {
            token.ThrowIfCancellationRequested();
            if (contour.Length < 3) continue;
            var rotated = Cv2.MinAreaRect(contour);
            if (Math.Min(rotated.Size.Width, rotated.Size.Height) < 3) continue;
            var bounds = Cv2.BoundingRect(contour);
            double score = 0; int count = 0;
            // Score the contour, not its enclosing rectangle (important for slanted text).
            using (var regionMask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.All(0)))
            {
                var shifted = contour.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)).ToArray();
                Cv2.FillPoly(regionMask, new[] { shifted }, Scalar.All(255));
                var maskBytes = new byte[bounds.Width * bounds.Height];
                Marshal.Copy(regionMask.Data, maskBytes, 0, maskBytes.Length);
                for (int y = 0; y < bounds.Height; y++)
                for (int x = 0; x < bounds.Width; x++)
                    if (maskBytes[y * bounds.Width + x] != 0) { score += probabilities[(bounds.Y + y) * mapW + bounds.X + x]; count++; }
            }
            if (count == 0 || score / count < .6) continue;
            double area = Cv2.ContourArea(contour), perimeter = Cv2.ArcLength(contour, true);
            float expand = (float)(area * 1.5 / Math.Max(1, perimeter));
            rotated.Size = new Size2f(rotated.Size.Width + 2 * expand, rotated.Size.Height + 2 * expand);
            var points = rotated.Points().Select(p => new Point2f(
                Math.Clamp(p.X * padded.Width / mapW - border, 0, image.Width - 1) + offsetX,
                Math.Clamp(p.Y * padded.Height / mapH - border, 0, image.Height - 1) + offsetY)).ToArray();
            int left = (int)Math.Floor(points.Min(p => p.X)), top = (int)Math.Floor(points.Min(p => p.Y));
            int right = (int)Math.Ceiling(points.Max(p => p.X)), bottom = (int)Math.Ceiling(points.Max(p => p.Y));
            if (right - left >= 3 && bottom - top >= 3) result.Add(new(points, new(left, top, right - left, bottom - top), offsetX));
        }
        return result;
    }

    private static Mat CropLine(Mat image, Point2f[] points)
    {
        var ordered = points.OrderBy(p => p.X).ToArray();
        var left = ordered.Take(2).OrderBy(p => p.Y).ToArray();
        var right = ordered.Skip(2).OrderBy(p => p.Y).ToArray();
        Point2f[] corners = { left[0], right[0], right[1], left[1] };
        static double Distance(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        int width = Math.Max(4, (int)Math.Round(Math.Max(Distance(corners[0], corners[1]), Distance(corners[2], corners[3]))));
        int height = Math.Max(4, (int)Math.Round(Math.Max(Distance(corners[0], corners[3]), Distance(corners[1], corners[2]))));
        using var transform = Cv2.GetPerspectiveTransform(corners, new Point2f[] { new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1) });
        var crop = new Mat();
        Cv2.WarpPerspective(image, crop, transform, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return crop;
    }

    private (string Text, float Score) ReadLine(Mat image, CancellationToken token)
    {
        const int height = 48;
        int resizedWidth = Math.Max(1, (int)Math.Ceiling(height * (double)image.Width / image.Height));
        if (resizedWidth > 4096) throw new InvalidOperationException("单行文字过长，请缩小选区后分段识别。");
        int width = Math.Max(320, resizedWidth);
        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(resizedWidth, height));
        using var output = Run(recognizer, ToTensor(resized, width, false), token);
        var logits = output.First().AsTensor<float>();
        int steps = logits.Dimensions[1], classes = logits.Dimensions[2];
        if (classes != characters.Length) throw new InvalidDataException("OCR 字典与识别模型不匹配，请重新解压完整发布包。");
        var values = logits.ToArray();
        var text = new StringBuilder();
        int previous = -1, count = 0; float confidence = 0;
        for (int t = 0; t < steps; t++)
        {
            int best = 0, start = t * classes;
            for (int c = 1; c < classes; c++) if (values[start + c] > values[start + best]) best = c;
            if (best != 0 && best != previous) { text.Append(characters[best]); confidence += values[start + best]; count++; }
            previous = best;
        }
        return (text.ToString(), count == 0 ? 0 : confidence / count);
    }

    private static DenseTensor<float> ToTensor(Mat image, int paddedWidth, bool detection)
    {
        int height = image.Height, width = image.Width;
        byte[] bytes = new byte[width * height * 3];
        Marshal.Copy(image.Data, bytes, 0, bytes.Length);
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, paddedWidth });
        Span<float> target = tensor.Buffer.Span;
        float[] means = { .485f, .456f, .406f }, deviations = { .229f, .224f, .225f };
        for (int c = 0; c < 3; c++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float value = bytes[(y * width + x) * 3 + c] / 255f;
            target[c * height * paddedWidth + y * paddedWidth + x] = detection ? (value - means[c]) / deviations[c] : (value - .5f) / .5f;
        }
        return tensor;
    }

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(InferenceSession session, DenseTensor<float> input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var registration = token.Register(() => options.Terminate = true);
        try { return session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input) }, session.OutputMetadata.Keys.ToArray(), options); }
        catch (OnnxRuntimeException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    internal static IReadOnlyList<OcrLine> OrderLines(IReadOnlyList<OcrLine> input)
    {
        // Recursive whitespace cuts read separate columns before moving right.
        if (input.Count <= 1) return input.ToArray();
        var byX = input.OrderBy(l => l.Bounds.X).ToArray();
        double typicalHeight = input.Select(l => l.Bounds.Height).OrderBy(h => h).ElementAt(input.Count / 2);
        int end = byX[0].Bounds.Right;
        for (int i = 1; i < byX.Length; i++)
        {
            if (byX[i].Bounds.X - end > typicalHeight * 2)
                return OrderLines(byX.Take(i).ToArray()).Concat(OrderLines(byX.Skip(i).ToArray())).ToArray();
            end = Math.Max(end, byX[i].Bounds.Right);
        }
        var byY = input.OrderBy(l => l.Bounds.Y).ToArray();
        end = byY[0].Bounds.Bottom;
        for (int i = 1; i < byY.Length; i++)
        {
            if (byY[i].Bounds.Y > end)
                return OrderLines(byY.Take(i).ToArray()).Concat(OrderLines(byY.Skip(i).ToArray())).ToArray();
            end = Math.Max(end, byY[i].Bounds.Bottom);
        }
        return input.OrderBy(l => l.Bounds.X).ToArray();
    }

    public void Dispose() { detector.Dispose(); recognizer.Dispose(); }
}
