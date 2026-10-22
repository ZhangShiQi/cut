using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Cut.Core;
using Cut.Services;

namespace Cut;

public sealed partial class OverlayWindow : Window
{
    private readonly CaptureSession session;
    private readonly SelectionSurface surface;
    private readonly Canvas toolsCanvas = new();
    private readonly Border toolbar;
    private readonly TextBlock statusText;
    private readonly WrapPanel buttons;
    internal Button AiButton { get; }
    private readonly Button cancelButton;
    private static readonly ControlTemplate ToolbarButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"
        <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
          <Border x:Name='Frame' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}' CornerRadius='3'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' Property='Opacity' Value='0.8'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Frame' Property='Opacity' Value='0.35'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='#46D3B5'/><Setter TargetName='Frame' Property='BorderThickness' Value='1'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>");
    public MonitorSnapshot Monitor { get; }

    public OverlayWindow(CaptureSession session, MonitorSnapshot monitor)
    {
        this.session = session;
        Monitor = monitor;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "Cut · 截图";
        surface = new SelectionSurface(session, monitor) { Focusable = true };
        var grid = new Grid();
        grid.Children.Add(surface);
        grid.Children.Add(toolsCanvas);
        Content = grid;
        buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        statusText = new TextBlock { Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(8, 4, 8, 8), Visibility = Visibility.Collapsed };
        var toolbarContent = new StackPanel();
        AddToolbarGrip(toolbarContent);
        toolbarContent.Children.Add(statusText);
        AddAnnotationToolbar(toolbarContent);
        toolbarContent.Children.Add(buttons);
        toolbar = new Border { Background = new SolidColorBrush(Color.FromRgb(25, 34, 49)), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = new ScrollViewer { Content = toolbarContent, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, Visibility = Visibility.Collapsed };
        AddButton(buttons, "识别二维码", "Ctrl + Q", async (_, _) => await session.RecognizeAsync(this));
        AddButton(buttons, "识别文字", "Ctrl + T", async (_, _) => await session.RecognizeTextAsync(this));
        AiButton = AddButton(buttons, "✦ AI 看图", "Ctrl + I", (_, _) => session.AnalyzeImage(this));
        AddButton(buttons, "长截图", "Ctrl + L · 手动或自动滚动，自动拼接", (_, _) => session.StartScroll(this));
        AddButton(buttons, "保存", "Ctrl + S", (_, _) => session.Save(this));
        cancelButton = AddButton(buttons, "取消", "Esc", (_, _) => session.Cancel());
        AddButton(buttons, "复制 ✓", "Enter / Ctrl + C", async (_, _) => await session.CopyAsync(this));
        toolsCanvas.Children.Add(toolbar);
        SourceInitialized += (_, _) => PositionWindow();
        Loaded += (_, _) => { PositionWindow(); surface.Focus(); Refresh(false); };
        SizeChanged += (_, _) => Refresh(toolbar.Visibility == Visibility.Visible);
        PreviewKeyDown += OnKeyDown;
        surface.MouseLeftButtonDown += OnMouseDown;
        surface.MouseMove += OnMouseMove;
        surface.MouseLeftButtonUp += OnMouseUp;
        surface.MouseRightButtonDown += (_, e) => { surface.ReleaseMouseCapture(); session.RightClick(); e.Handled = true; };
        surface.LostMouseCapture += (_, _) =>
        {
            if (session.Annotations?.IsDragging == true) { session.Annotations.CancelDraft(); session.Update(); }
            if (!session.Interaction.IsPointerDown) return;
            session.Interaction.LoseCapture();
            session.EndDrag(NativeMethods.CursorPosition());
        };
        Closing += (_, e) =>
        {
            if (session.IsFinished) return;
            // Alt+F4 must cancel the whole session, including other monitors.
            e.Cancel = true;
            Dispatcher.BeginInvoke(new Action(session.Abort));
        };
    }
    private void PositionWindow()
    {
        var r = Monitor.Bounds;
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), r.X, r.Y, r.Width, r.Height, 0x0010);
    }
    private static Button AddButton(Panel panel, string text, string hint, RoutedEventHandler handler)
    {
        var button = new Button { Content = text, ToolTip = hint, Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(3), Background = new SolidColorBrush(Color.FromRgb(42, 55, 74)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        button.Template = ToolbarButtonTemplate;
        button.Click += handler;
        panel.Children.Add(button);
        return button;
    }
    private int HitTolerance => Math.Max(5, (int)Math.Round(6 * Monitor.Bounds.Width / Math.Max(1, surface.ActualWidth)));
    private async void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (session.Busy) return;
        session.ClearStatus();
        var p = NativeMethods.CursorPosition();
        if (BeginAnnotation(p, e.ClickCount)) { e.Handled = true; return; }
        session.CommitText();
        if (e.ClickCount == 2 && session.Selection.Selection.Contains(p)) { await session.CopyAsync(this); return; }
        var dpi = VisualTreeHelper.GetDpi(surface);
        session.Interaction.Press(p, HitTolerance, (int)Math.Ceiling(SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX), (int)Math.Ceiling(SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY));
        surface.CaptureMouse();
        session.Update();
        e.Handled = true;
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (session.Busy) return;
        var p = NativeMethods.CursorPosition();
        if (session.AnnotationMode)
        {
            session.Annotations!.Move(session.AnnotationPoint(p));
            if (session.Annotations.IsDragging) session.Update();
            surface.Cursor = session.Annotations.Tool == MarkTool.Select ? Cursors.Arrow : session.Annotations.Tool == MarkTool.Text ? Cursors.IBeam : Cursors.Cross;
            return;
        }
        var previous = session.Interaction.DisplaySelection;
        bool wasPreview = session.Interaction.IsPreview;
        session.Interaction.Move(p);
        if (previous != session.Interaction.DisplaySelection || wasPreview != session.Interaction.IsPreview) session.Update();
        var edge = session.Selection.HitEdge(p, HitTolerance);
        surface.Cursor = edge switch
        {
            SelectionEdge.Left or SelectionEdge.Right => Cursors.SizeWE,
            SelectionEdge.Top or SelectionEdge.Bottom => Cursors.SizeNS,
            SelectionEdge.Left | SelectionEdge.Top or SelectionEdge.Right | SelectionEdge.Bottom => Cursors.SizeNWSE,
            SelectionEdge.Left | SelectionEdge.Bottom or SelectionEdge.Right | SelectionEdge.Top => Cursors.SizeNESW,
            _ => session.Selection.Selection.Contains(p) ? Cursors.SizeAll : Cursors.Cross
        };
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (session.Annotations?.IsDragging == true)
        {
            session.Annotations.End(session.AnnotationPoint(NativeMethods.CursorPosition()));
            surface.ReleaseMouseCapture(); session.Update(); e.Handled = true; return;
        }
        if (!session.Interaction.IsPointerDown) return;
        var p = NativeMethods.CursorPosition();
        session.Interaction.Release(p);
        session.EndDrag(p);
        surface.ReleaseMouseCapture();
        e.Handled = true;
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = await HandleKeyAsync(e.Key, Keyboard.Modifiers);
    }
    internal async System.Threading.Tasks.Task<bool> HandleKeyAsync(Key key, ModifierKeys modifiers)
    {
        if (textEditor?.IsKeyboardFocusWithin == true)
        {
            if (key == Key.Escape) { CancelTextEditor(); session.Update(); return true; }
            if (key == Key.Enter && modifiers == ModifierKeys.Control) { CommitTextEditor(); surface.Focus(); return true; }
            return false;
        }
        if (key == Key.Escape) { session.Cancel(); return true; }
        if (session.Busy || session.Interaction.IsPointerDown || session.Annotations?.IsDragging == true) return false;
        if (key == Key.Z && modifiers == ModifierKeys.Control) { session.UndoMark(); return true; }
        if (key == Key.Y && modifiers == ModifierKeys.Control) { session.RedoMark(); return true; }
        if (key == Key.Delete && modifiers == ModifierKeys.None) { session.Annotations?.DeleteSelected(); session.Update(); return true; }
        if (key == Key.Enter || (modifiers == ModifierKeys.Control && key is Key.C or Key.S or Key.Q or Key.T or Key.I or Key.L)) session.ConfirmPreview();
        if (key == Key.Q && modifiers == ModifierKeys.Control) await session.RecognizeAsync(this);
        else if (key == Key.T && modifiers == ModifierKeys.Control) await session.RecognizeTextAsync(this);
        else if (key == Key.I && modifiers == ModifierKeys.Control) session.AnalyzeImage(this);
        else if (key == Key.L && modifiers == ModifierKeys.Control) session.StartScroll(this);
        else if (key == Key.Enter || (key == Key.C && modifiers == ModifierKeys.Control)) await session.CopyAsync(this);
        else if (key == Key.S && modifiers == ModifierKeys.Control) session.Save(this);
        else return false;
        return true;
    }
    public void Refresh(bool showToolbar)
    {
        surface.InvalidateVisual();
        toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
        statusText.Text = session.StatusMessage;
        statusText.Visibility = string.IsNullOrEmpty(session.StatusMessage) ? Visibility.Collapsed : Visibility.Visible;
        foreach (Button button in buttons.Children) button.IsEnabled = !session.Busy || (button == cancelButton && session.IsRecognizing);
        RefreshAnnotationToolbar();
        if (!showToolbar || ActualWidth <= 0 || ActualHeight <= 0) return;
        toolbar.MaxWidth = Math.Max(1, ActualWidth - 16);
        toolbar.MaxHeight = Math.Max(50, Math.Min(280, ActualHeight * .6));
        toolbar.Measure(new Size(toolbar.MaxWidth, double.PositiveInfinity));
        var part = session.Selection.Selection.Intersect(Monitor.Bounds);
        double sx = ActualWidth / Monitor.Bounds.Width, sy = ActualHeight / Monitor.Bounds.Height;
        double x = (part.Right - Monitor.Bounds.X) * sx - toolbar.DesiredSize.Width;
        double y = (part.Bottom - Monitor.Bounds.Y) * sy + 10;
        if (y + toolbar.DesiredSize.Height > ActualHeight - 8) y = (part.Y - Monitor.Bounds.Y) * sy - toolbar.DesiredSize.Height - 10;
        PositionToolbar(toolbarPosition ?? new Point(x, y));
    }
}

internal sealed class SelectionSurface : FrameworkElement
{
    private readonly CaptureSession session;
    private readonly MonitorSnapshot monitor;
    private static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(70, 211, 181));
    public SelectionSurface(CaptureSession session, MonitorSnapshot monitor) { this.session = session; this.monitor = monitor; Cursor = Cursors.Cross; ClipToBounds = true; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawImage(monitor.Image, new Rect(0, 0, w, h));
        var selection = session.Interaction.DisplaySelection;
        var part = selection.Intersect(monitor.Bounds);
        if (part.IsEmpty)
        {
            dc.DrawRectangle(Shade, null, new Rect(0, 0, w, h));
            if (selection.IsEmpty) DrawLabel(dc, "移动鼠标预选窗口  ·  拖拽自由框选  ·  Esc 取消", 24, 24, w);
            return;
        }
        double sx = w / monitor.Bounds.Width, sy = h / monitor.Bounds.Height;
        var r = new Rect((part.X - monitor.Bounds.X) * sx, (part.Y - monitor.Bounds.Y) * sy, part.Width * sx, part.Height * sy);
        dc.DrawRectangle(Shade, null, new Rect(0, 0, w, r.Top));
        dc.DrawRectangle(Shade, null, new Rect(0, r.Bottom, w, Math.Max(0, h - r.Bottom)));
        dc.DrawRectangle(Shade, null, new Rect(0, r.Top, r.Left, r.Height));
        dc.DrawRectangle(Shade, null, new Rect(r.Right, r.Top, Math.Max(0, w - r.Right), r.Height));
        var full = new Rect((selection.X - monitor.Bounds.X) * sx, (selection.Y - monitor.Bounds.Y) * sy, selection.Width * sx, selection.Height * sy);
        if (session.Annotations is not null && session.AnnotationRenderer is not null)
        {
            dc.PushTransform(new TranslateTransform((session.AnnotationBounds.X - monitor.Bounds.X) * sx, (session.AnnotationBounds.Y - monitor.Bounds.Y) * sy));
            dc.PushTransform(new ScaleTransform(sx, sy));
            session.AnnotationRenderer.Draw(dc, session.Annotations, controls: true, handleSize: 6 / Math.Max(.1, sx));
            dc.Pop(); dc.Pop();
        }
        dc.DrawRectangle(null, new Pen(Accent, 1.5), full);
        if (!session.Interaction.IsPreview && session.Annotations?.Locked != true)
        foreach (double x in new[] { full.Left, full.Left + full.Width / 2, full.Right })
            foreach (double y in new[] { full.Top, full.Top + full.Height / 2, full.Bottom })
                if (x != full.Left + full.Width / 2 || y != full.Top + full.Height / 2)
                    dc.DrawRectangle(Brushes.White, new Pen(Accent, 1), new Rect(x - 3, y - 3, 6, 6));
        string label = $"{selection.Width} × {selection.Height} px";
        if (session.Interaction.IsPreview) label += "  ·  单击选定，拖拽框选";
        else if (session.Annotations?.Locked == true) label += "  ·  标注中，选区已锁定";
        DrawLabel(dc, label, Math.Max(8, r.Left), r.Top >= 35 ? r.Top - 31 : r.Top + 9, w);
    }
    private void DrawLabel(DrawingContext dc, string value, double x, double y, double width)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        x = Math.Min(x, Math.Max(0, width - text.Width - 20));
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(25, 34, 49)), null, new Rect(x, y, text.Width + 20, text.Height + 12), 4, 4);
        dc.DrawText(text, new Point(x + 10, y + 6));
    }
}
