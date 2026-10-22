using System;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cut.Core;

internal static class AnnotationDrawing
{
    public static FormattedText Text(Annotation a) => new(a.Text.Length == 0 ? " " : a.Text, CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), a.Style.FontSize, new SolidColorBrush(a.Style.Color), 1)
        { MaxTextWidth = Math.Max(1, a.TextWidth) };
    public static Rect Bounds(Annotation a)
    {
        if (a.Tool == MarkTool.Text) { var t = Text(a); return new(a.Start, new Size(Math.Max(1, t.WidthIncludingTrailingWhitespace), t.Height)); }
        if (a.Tool is MarkTool.Pen or MarkTool.Mosaic && a.Points.Length > 0)
        {
            double x = a.Points.Min(p => p.X), y = a.Points.Min(p => p.Y);
            return new(x, y, Math.Max(1, a.Points.Max(p => p.X) - x), Math.Max(1, a.Points.Max(p => p.Y) - y));
        }
        return new(a.Start, a.End);
    }
    public static Point[] Handles(Annotation a) => a.Tool switch
    {
        MarkTool.Arrow => new[] { a.Start, a.End },
        MarkTool.Rectangle or MarkTool.Ellipse or MarkTool.Cover => new[] { Bounds(a).TopLeft, Bounds(a).TopRight, Bounds(a).BottomRight, Bounds(a).BottomLeft },
        _ => Array.Empty<Point>()
    };
    private static Pen Pen(Annotation a, double extra = 0) => new(new SolidColorBrush(a.Style.Color), a.Style.Width + extra)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    public static Geometry Path(Annotation a)
    {
        if (a.Tool == MarkTool.Ellipse) return new EllipseGeometry(Bounds(a));
        if (a.Tool is MarkTool.Rectangle or MarkTool.Cover or MarkTool.Text) return new RectangleGeometry(Bounds(a));
        var path = new StreamGeometry();
        using (var dc = path.Open())
        {
            dc.BeginFigure(a.Start, false, false);
            if (a.Tool is MarkTool.Pen or MarkTool.Mosaic && a.Points.Length > 1) dc.PolyLineTo(a.Points.Skip(1).ToArray(), true, false);
            else dc.LineTo(a.End, true, false);
        }
        path.Freeze(); return path;
    }
    private static Geometry ArrowHead(Annotation a)
    {
        Vector direction = a.End - a.Start;
        if (direction.Length < .01) return Geometry.Empty;
        double size = Math.Min(direction.Length, Math.Max(12, a.Style.Width * 4)); direction.Normalize();
        var side = new Vector(-direction.Y, direction.X) * size * .45;
        var path = new StreamGeometry();
        using (var dc = path.Open())
        { dc.BeginFigure(a.End, true, true); dc.LineTo(a.End - direction * size + side, true, false); dc.LineTo(a.End - direction * size - side, true, false); }
        path.Freeze(); return path;
    }
    public static bool Hit(Annotation a, Point point, double tolerance)
    {
        if (a.Tool == MarkTool.Pen && a.Points.Length == 1) return (point - a.Start).Length <= a.Style.Width / 2 + tolerance;
        var path = Path(a);
        if (a.Tool is MarkTool.Text or MarkTool.Cover) return path.FillContains(point);
        return path.StrokeContains(Pen(a, tolerance * 2), point) || (a.Tool == MarkTool.Arrow && ArrowHead(a).FillContains(point));
    }
    public static void Draw(DrawingContext dc, Annotation a, Func<BitmapSource> mosaic, int width, int height)
    {
        var brush = new SolidColorBrush(a.Style.Color);
        if (a.Tool == MarkTool.Text) { dc.DrawText(Text(a), a.Start); return; }
        if (a.Tool == MarkTool.Cover)
        {
            var b = Bounds(a);
            var r = new Rect(Math.Floor(b.Left), Math.Floor(b.Top), Math.Ceiling(b.Right) - Math.Floor(b.Left), Math.Ceiling(b.Bottom) - Math.Floor(b.Top));
            dc.DrawRectangle(brush, null, r); return;
        }
        if (a.Tool == MarkTool.Mosaic)
        {
            double size = a.Style.Width * 8;
            Geometry mask = a.Points.Length == 1 ? new EllipseGeometry(a.Start, size / 2, size / 2) :
                Path(a).GetWidenedPathGeometry(new Pen(Brushes.Black, size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            dc.PushClip(mask); dc.DrawImage(mosaic(), new Rect(0, 0, width, height)); dc.Pop(); return;
        }
        if (a.Tool == MarkTool.Pen && a.Points.Length == 1) dc.DrawEllipse(brush, null, a.Start, a.Style.Width / 2, a.Style.Width / 2);
        else dc.DrawGeometry(null, Pen(a), Path(a));
        if (a.Tool == MarkTool.Arrow) dc.DrawGeometry(brush, null, ArrowHead(a));
    }
}

internal sealed class AnnotationRenderer
{
    private readonly BitmapSource background;

    public AnnotationRenderer(BitmapSource background) => this.background = background;
    private static BitmapSource Pixelate(BitmapSource background)
    {
        int width = background.PixelWidth, height = background.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        new FormatConvertedBitmap(background, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, stride, 0);
        const int block = 16;
        for (int y = 0; y < height; y += block)
        for (int x = 0; x < width; x += block)
        {
            int right = Math.Min(width, x + block), bottom = Math.Min(height, y + block), count = (right - x) * (bottom - y);
            int b = 0, g = 0, r = 0;
            for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
            { int i = py * stride + px * 4; b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; }
            for (int py = y; py < bottom; py++) for (int px = x; px < right; px++)
            { int i = py * stride + px * 4; pixels[i] = (byte)(b / count); pixels[i + 1] = (byte)(g / count); pixels[i + 2] = (byte)(r / count); }
        }
        var mosaic = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, stride); mosaic.Freeze(); return mosaic;
    }
    private BitmapSource Raster(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) draw(dc);
        var bitmap = new RenderTargetBitmap(background.PixelWidth, background.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    private Annotation[] prefix = Array.Empty<Annotation>();
    private BitmapSource? prefixImage, pixelatedPrefix;
    private BitmapSource Prefix(Annotation[] items)
    {
        if (prefixImage is not null && prefix.SequenceEqual(items)) return prefixImage;
        BitmapSource current = background;
        var batch = new List<Annotation>();
        void Flush()
        {
            if (batch.Count == 0) return;
            var previous = current;
            current = Raster(dc => {
                dc.DrawImage(previous, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
                foreach (var mark in batch) AnnotationDrawing.Draw(dc, mark, () => throw new InvalidOperationException(), background.PixelWidth, background.PixelHeight);
            });
            batch.Clear();
        }
        foreach (var a in items)
        {
            if (a.Tool != MarkTool.Mosaic) { batch.Add(a); continue; }
            Flush();
            var previous = current;
            var pixels = Pixelate(current);
            current = Raster(dc => {
                dc.DrawImage(previous, new Rect(0, 0, background.PixelWidth, background.PixelHeight));
                AnnotationDrawing.Draw(dc, a, () => pixels, background.PixelWidth, background.PixelHeight);
            });
        }
        Flush();
        prefix = items; prefixImage = current; pixelatedPrefix = null;
        return current;
    }
    public void Draw(DrawingContext dc, AnnotationDocument document, bool controls = false, double handleSize = 6)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, document.Width, document.Height)));
        var items = document.VisibleItems.ToArray();
        if (items.Length > 0)
        {
            // Cache all but the active last mark, so a long mosaic stroke only pixelates once.
            var baseImage = Prefix(items.Take(items.Length - 1).ToArray());
            dc.DrawImage(baseImage, new Rect(0, 0, document.Width, document.Height));
            AnnotationDrawing.Draw(dc, items[^1], () => pixelatedPrefix ??= Pixelate(baseImage), document.Width, document.Height);
        }
        if (controls && document.Tool == MarkTool.Select && document.Selected is not null && !document.EditingText)
        {
            var a = document.Draft ?? document.Selected;
            var pen = new Pen(Brushes.DodgerBlue, 1) { DashStyle = DashStyles.Dash };
            dc.DrawRectangle(null, pen, AnnotationDrawing.Bounds(a));
            foreach (var point in AnnotationDrawing.Handles(a))
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, 1), new Rect(point.X - handleSize / 2, point.Y - handleSize / 2, handleSize, handleSize));
        }
        dc.Pop();
    }
    public BitmapSource Render(AnnotationDocument document)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        { dc.DrawImage(background, new Rect(0, 0, document.Width, document.Height)); Draw(dc, document); }
        var bitmap = new RenderTargetBitmap(document.Width, document.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
