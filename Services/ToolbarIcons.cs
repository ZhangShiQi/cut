using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;

namespace Cut.Services;

// The bundled SVGs deliberately use only paths and a 24x24 viewBox. Render their
// original geometry as WPF vectors without rasterization or an external renderer.
internal static class ToolbarIcons
{
    private static readonly Dictionary<(string, Color), DrawingImage> cache = new();
    internal static string Name(string label) => label switch
    {
        "选择" => "select", "矩形" => "rectangle", "椭圆" => "ellipse", "箭头" => "arrow",
        "画笔" => "pen", "文字" => "text", "马赛克" => "mosaic", "实色遮挡" => "cover",
        "撤销" => "undo", "重做" => "redo", "重新框选" => "reselect", "识别二维码" => "qr",
        "识别文字" => "ocr", "✦ AI 看图" or "AI 看图" => "ai", "长截图" => "scroll",
        "保存" or "保存 PNG" => "save", "取消" or "关闭" => "close", "复制 ✓" or "复制图片" => "copy",
        "删除" => "delete", "框选局部" => "crop", "全图" => "full", "适应宽度" => "fit",
        "100%" => "actual", "－" => "zoom-out", "＋" => "zoom-in", "标注颜色" => "color",
        "拖动工具栏" => "move", _ => throw new ArgumentException("Missing toolbar SVG: " + label)
    };
    internal static Image Create(string label, Color color, double size = 20) => new()
    { Source = Load(Name(label), color), Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private static DrawingImage Load(string name, Color color)
    {
        if (cache.TryGetValue((name, color), out var existing)) return existing;
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Toolbar/" + name + ".svg"));
        using var stream = resource.Stream;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root!;
        if (root.Attribute("viewBox")?.Value != "0 0 24 24" || root.Elements().Any(e => e.Name.LocalName != "path"))
            throw new InvalidOperationException("Unsupported bundled SVG: " + name);
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        Brush? Paint(string? value) => value == "none" ? null : value is null or "currentColor" ? new SolidColorBrush(color) : (Brush)new BrushConverter().ConvertFromInvariantString(value)!;
        foreach (var element in root.Elements())
        {
            var stroke = Paint(element.Attribute("stroke")?.Value);
            var pen = stroke is null ? null : new Pen(stroke, double.Parse(element.Attribute("stroke-width")?.Value ?? "1.8", CultureInfo.InvariantCulture))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            drawing.Children.Add(new GeometryDrawing(Paint(element.Attribute("fill")?.Value ?? "none"), pen, Geometry.Parse(element.Attribute("d")!.Value)));
        }
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); cache.Add((name, color), image); return image;
    }
    internal static void Apply(Button button, string label, Color color, string? hint = null)
    {
        button.Content = Create(label, color); button.Width = button.Height = 36;
        button.Padding = new Thickness(4); button.Margin = new Thickness(3);
        button.ToolTip = string.IsNullOrEmpty(hint) ? label : label + " · " + hint;
        AutomationProperties.SetName(button, label.Replace("✦ ", "").Replace(" ✓", ""));
        AutomationProperties.SetHelpText(button, button.ToolTip.ToString());
    }
}
