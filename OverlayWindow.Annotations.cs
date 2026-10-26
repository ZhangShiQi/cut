using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cut.Core;
using Cut.Services;

namespace Cut;

public sealed partial class OverlayWindow
{
    private readonly Dictionary<MarkTool, Button> markButtons = new();
    private readonly Dictionary<Color, Button> colorButtons = new();
    private readonly ComboBox widthChoice = new() { Width = 65, Height = 28, Margin = new Thickness(4) };
    private readonly ComboBox fontChoice = new() { Width = 65, Height = 28, Margin = new Thickness(4) };
    private Button undoButton = null!, redoButton = null!;
    private bool syncingStyle;
    private TextBox? textEditor;
    internal TextBox? ActiveTextEditor => textEditor;
    private Point? toolbarPosition;
    internal void ReleaseAnnotationCapture()
    {
        surface.ReleaseMouseCapture();
        if (!session.AnnotationMode) surface.Cursor = Cursors.Arrow;
    }
    private void PositionToolbar(Point position)
    {
        Canvas.SetLeft(toolbar, Math.Clamp(position.X, 8, Math.Max(8, ActualWidth - toolbar.DesiredSize.Width - 8)));
        Canvas.SetTop(toolbar, Math.Clamp(position.Y, 8, Math.Max(8, ActualHeight - toolbar.DesiredSize.Height - 8)));
    }
    private void AddToolbarGrip(StackPanel content)
    {
        var grip = new Border { Child = ToolbarIcons.Create("拖动工具栏", Colors.LightGray, 16), Padding = new Thickness(8, 3, 8, 3), Cursor = Cursors.SizeAll, Background = Brushes.Transparent, ToolTip = "拖动工具栏" };
        Point anchor = default, origin = default;
        grip.MouseLeftButtonDown += (_, e) =>
        {
            anchor = e.GetPosition(toolsCanvas); origin = new(Canvas.GetLeft(toolbar), Canvas.GetTop(toolbar));
            grip.CaptureMouse(); e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!grip.IsMouseCaptured) return;
            toolbarPosition = origin + (e.GetPosition(toolsCanvas) - anchor); PositionToolbar(toolbarPosition.Value);
        };
        grip.MouseLeftButtonUp += (_, e) => { grip.ReleaseMouseCapture(); e.Handled = true; };
        content.Children.Add(grip);
    }

    private void AddAnnotationToolbar(StackPanel content)
    {
        var tools = new WrapPanel();
        string[] labels = { "选择", "矩形", "椭圆", "箭头", "画笔", "文字", "马赛克", "实色遮挡" };
        foreach (MarkTool tool in Enum.GetValues<MarkTool>())
        {
            var choice = tool;
            string hint = tool switch { MarkTool.Select => "选择、移动和调整标注；双击文字可编辑", MarkTool.Text => "点击输入文字；Enter 换行，Ctrl + Enter 完成，Esc 取消", MarkTool.Mosaic => "拖动涂抹；可撤销重做，不能单独选择移动", MarkTool.Cover => "实色覆盖；适合遮挡敏感内容", _ => labels[(int)tool] };
            markButtons[tool] = AddButton(tools, labels[(int)tool], hint,
                (_, _) => { session.SetTool(choice); surface.Focus(); });
        }
        content.Children.Add(tools);
        var styles = new WrapPanel { Margin = new Thickness(3, 2, 3, 2) };
        foreach (var color in new[] { Colors.Red, Colors.Gold, Colors.LimeGreen, Colors.DodgerBlue, Colors.Black, Colors.White })
        {
            var selected = color;
            var button = new Button { Background = new SolidColorBrush(Color.FromRgb(42, 55, 74)), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            ToolbarIcons.Apply(button, "标注颜色", color, color.ToString());
            button.Template = ToolbarButtonTemplate;
            button.Click += (_, _) =>
            {
                var doc = session.Annotations;
                if (doc is null) { session.SetTool(MarkTool.Rectangle); doc = session.Annotations; }
                if (doc is not null) session.ChangeMarkStyle(doc.Style with { Color = selected });
            };
            colorButtons[color] = button; styles.Children.Add(button);
        }
        styles.Children.Add(new TextBlock { Text = "粗细", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) });
        syncingStyle = true;
        foreach (double value in new[] { 2.0, 4.0, 8.0 }) widthChoice.Items.Add(new ComboBoxItem { Content = value == 2 ? "细" : value == 4 ? "中" : "粗", Tag = value });
        foreach (double value in new[] { 18.0, 28.0, 40.0, 56.0 }) fontChoice.Items.Add(new ComboBoxItem { Content = value.ToString(), Tag = value });
        widthChoice.SelectedIndex = 1; fontChoice.SelectedIndex = 1; syncingStyle = false;
        widthChoice.SelectionChanged += (_, _) => ChangeStyleFromControls();
        fontChoice.SelectionChanged += (_, _) => ChangeStyleFromControls();
        styles.Children.Add(widthChoice);
        styles.Children.Add(new TextBlock { Text = "字号", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) });
        styles.Children.Add(fontChoice); content.Children.Add(styles);
        undoButton = AddButton(buttons, "撤销", "Ctrl + Z", (_, _) => session.UndoMark());
        redoButton = AddButton(buttons, "重做", "Ctrl + Y", (_, _) => session.RedoMark());
        AddButton(buttons, "重新框选", "清空标注并重新框选", (_, _) => session.Reselect(this));
    }
    private void ChangeStyleFromControls()
    {
        if (syncingStyle) return;
        var doc = session.Annotations;
        if (doc is null) { session.SetTool(MarkTool.Rectangle); doc = session.Annotations; }
        if (doc is null) return;
        session.ChangeMarkStyle(doc.Style with
        {
            Width = widthChoice.SelectedItem is ComboBoxItem { Tag: double width } ? width : 4,
            FontSize = fontChoice.SelectedItem is ComboBoxItem { Tag: double size } ? size : 28
        });
    }
    private void RefreshAnnotationToolbar()
    {
        if (markButtons.Count == 0) return;
        var doc = session.Annotations; var style = doc?.Style ?? new MarkStyle(Colors.Red);
        bool enabled = !session.Busy && doc?.IsDragging != true;
        foreach (var entry in markButtons)
        { entry.Value.IsEnabled = enabled; entry.Value.Background = new SolidColorBrush(session.AnnotationMode && entry.Key == doc?.Tool ? Color.FromRgb(25, 123, 104) : Color.FromRgb(42, 55, 74)); }
        foreach (var entry in colorButtons)
        { entry.Value.IsEnabled = enabled && doc?.Tool != MarkTool.Mosaic; entry.Value.BorderThickness = new Thickness(entry.Key == style.Color ? 3 : 1); entry.Value.BorderBrush = entry.Key == style.Color ? Brushes.DeepSkyBlue : Brushes.Gray; }
        widthChoice.IsEnabled = enabled; fontChoice.IsEnabled = enabled;
        syncingStyle = true;
        foreach (ComboBoxItem item in widthChoice.Items) if ((double)item.Tag == style.Width) widthChoice.SelectedItem = item;
        foreach (ComboBoxItem item in fontChoice.Items) if ((double)item.Tag == style.FontSize) fontChoice.SelectedItem = item;
        syncingStyle = false;
        undoButton.IsEnabled = enabled && doc?.CanUndo == true;
        redoButton.IsEnabled = enabled && doc?.CanRedo == true;
    }
    internal bool BeginAnnotation(PixelPoint point, int clicks = 1)
    {
        if (!session.AnnotationMode) return false;
        session.CommitText();
        var doc = session.Annotations!;
        if (!session.AnnotationBounds.Contains(point)) return true;
        Point p = session.AnnotationPoint(point);
        int hit = doc.Hit(p, HitTolerance);
        if (doc.Tool == MarkTool.Text || (doc.Tool == MarkTool.Select && clicks == 2 && hit >= 0 && doc.Items[hit].Tool == MarkTool.Text))
        { StartTextEditor(p, doc.Tool == MarkTool.Select ? hit : -1); return true; }
        if (doc.Begin(p, HitTolerance)) surface.CaptureMouse();
        session.Update(); return true;
    }
    internal void StartTextEditor(Point point, int existing = -1)
    {
        session.CommitText();
        var doc = session.Annotations!;
        doc.BeginText(point, existing);
        var draft = doc.Draft!;
        double sx = ActualWidth / Monitor.Bounds.Width, sy = ActualHeight / Monitor.Bounds.Height;
        double x = (session.AnnotationBounds.X + draft.Start.X - Monitor.Bounds.X) * sx;
        double y = (session.AnnotationBounds.Y + draft.Start.Y - Monitor.Bounds.Y) * sy;
        textEditor = new TextBox
        {
            Text = draft.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, IsUndoEnabled = true, MaxLength = 4000,
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = draft.Style.FontSize * sy, Foreground = new SolidColorBrush(draft.Style.Color),
            Background = draft.Style.Color.R * .299 + draft.Style.Color.G * .587 + draft.Style.Color.B * .114 > 150 ? new SolidColorBrush(Color.FromRgb(25, 34, 49)) : Brushes.White,
            BorderBrush = Brushes.DodgerBlue, BorderThickness = new Thickness(1), Padding = new Thickness(2),
            Width = Math.Max(30, Math.Min(draft.TextWidth * sx + 6, ActualWidth - Math.Max(0, x) - 4)),
            MaxHeight = Math.Max(40, Math.Min(240, ActualHeight - Math.Max(0, y) - 4)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Canvas.SetLeft(textEditor, Math.Clamp(x, 0, Math.Max(0, ActualWidth - textEditor.Width)));
        Canvas.SetTop(textEditor, Math.Clamp(y, 0, Math.Max(0, ActualHeight - 40)));
        toolsCanvas.Children.Add(textEditor); Panel.SetZIndex(textEditor, 10);
        textEditor.TextChanged += (_, _) => { if (textEditor is not null) { doc.PreviewText(textEditor.Text); session.Update(); } };
        textEditor.Focus(); textEditor.CaretIndex = textEditor.Text.Length; session.Update();
    }
    internal void CommitTextEditor()
    {
        if (textEditor is null) return;
        string text = textEditor.Text; toolsCanvas.Children.Remove(textEditor); textEditor = null;
        session.Annotations?.CommitText(text); session.Update();
    }
    internal void CancelTextEditor()
    {
        if (textEditor is null) return;
        toolsCanvas.Children.Remove(textEditor); textEditor = null; session.Annotations?.CancelDraft();
    }
}
