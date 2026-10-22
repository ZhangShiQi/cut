using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal sealed class ScrollCaptureWindow : Window
{
    private readonly PixelRect area, monitor;
    private PixelRect contentArea;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly ComboBox direction = new() { Width = 170, Margin = new Thickness(4) };
    private readonly TextBox top = new() { Text = "0", Width = 50, Margin = new Thickness(4) }, bottom = new() { Text = "0", Width = 50, Margin = new Thickness(4) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.DarkSlateGray };
    private readonly TextBlock dimensions = new() { Margin = new Thickness(8), FontWeight = FontWeights.SemiBold };
    private readonly Image preview = new() { Height = 130, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly Button start, undo, finish;
    private Window? frame;
    private bool closed, inFlight, excluded;
    private int generation, stableCount;
    private ScrollFrame? candidate;
    private IntPtr target;
    private NativeMethods.Rect targetBounds;
    private string validationFailure = "目标窗口状态变化";
    internal bool Running { get; private set; }
    internal ScrollAssembler? Document { get; private set; }
    internal BitmapSource? Result { get; private set; }
    internal string Message => status.Text;
    internal Func<PixelRect, BitmapSource> Capture { get; set; } = CaptureService.CaptureRegion;
    internal Func<bool>? ValidateForTest { get; set; }
    internal Func<IntPtr>? ResolveTargetForTest { get; set; }
    internal bool AutomaticPolling { get; set; } = true;
    internal ScrollDirection Direction { get => direction.SelectedIndex == 1 ? ScrollDirection.Up : ScrollDirection.Down; set => direction.SelectedIndex = value == ScrollDirection.Up ? 1 : 0; }

    public ScrollCaptureWindow(PixelRect area, PixelRect monitor)
    {
        this.area = contentArea = area; this.monitor = monitor;
        Title = "Cut · 长截图"; Width = 390; Height = 430; MinWidth = 340; MinHeight = 360;
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = "手动滚动 · 自动拼接", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 8) });
        direction.Items.Add("向下 · 网页"); direction.Items.Add("向上 · 聊天历史"); direction.SelectedIndex = 0;
        var options = new WrapPanel(); options.Children.Add(direction);
        root.Children.Add(options);
        var trims = new WrapPanel();
        trims.Children.Add(new TextBlock { Text = "排除顶部 / 底部（像素）", VerticalAlignment = VerticalAlignment.Center });
        trims.Children.Add(top); trims.Children.Add(bottom); root.Children.Add(trims);
        var actions = new WrapPanel();
        start = Add(actions, "开始", async () => { try { await ToggleAsync(); } catch (Exception ex) { Pause("无法开始采集：" + ex.Message); } });
        undo = Add(actions, "撤销拼接", Undo);
        finish = Add(actions, "完成", Complete);
        Add(actions, "取消", Close); root.Children.Add(actions);
        root.Children.Add(dimensions); root.Children.Add(status); root.Children.Add(preview);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        status.Text = "只框选内容区，排除导航栏和输入框。点击开始，再把鼠标移到内容区滚动；建议每次约 1/3 屏，停半秒等待自动拼接。";
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            excluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && NativeMethods.SetWindowDisplayAffinity(hwnd, 0x11);
            PositionOutside();
        };
        Loaded += (_, _) => { PositionOutside(); UpdateControls(); };
        timer.Tick += async (_, _) => await PollAsync();
        Closed += (_, _) => { closed = true; Running = false; generation++; timer.Stop(); frame?.Close(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (Running) Pause("已暂停；可继续、完成或取消。"); else Close(); e.Handled = true; }
        };
    }
    private static Button Add(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(3) };
        button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    private void PositionOutside()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(Width * dpi.DpiScaleX), h = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        int x = area.Right + 10, y = area.Y;
        if (x + w > monitor.Right)
        {
            if (area.X - w - 10 >= monitor.X) x = area.X - w - 10;
            else { x = Math.Clamp(area.X, monitor.X, Math.Max(monitor.X, monitor.Right - w)); y = area.Bottom + 10; if (y + h > monitor.Bottom) y = area.Y - h - 10; }
        }
        x = Math.Clamp(x, monitor.X, Math.Max(monitor.X, monitor.Right - w));
        y = Math.Clamp(y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - h));
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, w, h, 0x0010);
    }
    private bool OwnWindow(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint process); return process == Environment.ProcessId;
    }
    private bool ValidateTarget()
    {
        if (ValidateForTest is not null) return ValidateForTest();
        if (!NativeMethods.IsWindow(target) || !NativeMethods.IsWindowVisible(target) || NativeMethods.IsIconic(target) || !NativeMethods.GetWindowRect(target, out var bounds)) return Reject("目标窗口已隐藏或关闭");
        if (bounds.Left != targetBounds.Left || bounds.Right != targetBounds.Right || bounds.Top != targetBounds.Top || bounds.Bottom != targetBounds.Bottom) return Reject("目标窗口位置或尺寸已变化");
        if (!excluded && NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var ownRect) &&
            !contentArea.Intersect(new(ownRect.Left, ownRect.Top, ownRect.Right - ownRect.Left, ownRect.Bottom - ownRect.Top)).IsEmpty) return Reject("当前系统无法排除工具栏，请把工具栏移到选区外");
        var foreground = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), 2);
        if (foreground != target && foreground != new WindowInteropHelper(this).Handle) return Reject("请先点击目标窗口使其回到前台");
        // Inspect the complete window rectangles as well as hit-test samples, so small occlusions
        // between sample points cannot silently enter the captured image.
        foreach (var window in WindowDetectionService.Capture(uint.MaxValue))
        {
            if (window.Handle == target) break;
            if (window.Handle == new WindowInteropHelper(this).Handle && excluded) continue;
            if (frame is not null && window.Handle == new WindowInteropHelper(frame).Handle) continue;
            if (!window.Bounds.Intersect(contentArea).IsEmpty) return Reject("选区被其他窗口遮挡，请移开后继续");
        }
        foreach (double x in new[] { .08, .5, .92 })
        foreach (double y in new[] { .08, .5, .92 })
        {
            var at = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = contentArea.X + (int)(contentArea.Width * x), Y = contentArea.Y + (int)(contentArea.Height * y) }), 2);
            bool controller = excluded && at == new WindowInteropHelper(this).Handle;
            bool border = frame is not null && at == new WindowInteropHelper(frame).Handle;
            if (at != target && !controller && !border) return Reject("选区内有其他窗口遮挡，请移开后继续");
        }
        return true;
    }
    private bool Reject(string reason) { validationFailure = reason; return false; }
    private void ShowFrame()
    {
        frame?.Close();
        frame = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, IsHitTestVisible = false,
            Content = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.MediumSpringGreen } };
        frame.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(frame).Handle;
            int style = NativeMethods.GetWindowLong(hwnd, -20);
            NativeMethods.SetWindowLong(hwnd, -20, style | 0x20 | 0x80 | 0x08000000);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) NativeMethods.SetWindowDisplayAffinity(hwnd, 0x11);
            // Border is outside the captured pixels even on systems without capture exclusion.
            NativeMethods.SetWindowPos(hwnd, new IntPtr(-1), contentArea.X - 4, contentArea.Y - 4, contentArea.Width + 8, contentArea.Height + 8, 0x0010);
        };
        frame.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(frame);
            ((Border)frame.Content).BorderThickness = new Thickness(2 / dpi.DpiScaleX, 2 / dpi.DpiScaleY, 2 / dpi.DpiScaleX, 2 / dpi.DpiScaleY);
        };
        frame.Show();
    }
    internal async Task ToggleAsync()
    {
        if (closed) return;
        if (Running) { Pause("已暂停。继续前请勿改变窗口位置或缩放。"); return; }
        if (Document is null)
        {
            if (!int.TryParse(top.Text, out int t) || !int.TryParse(bottom.Text, out int b) || t < 0 || b < 0 || (long)t + b > area.Height - 120)
            { status.Text = "顶部和底部需为非负整数，并至少保留 120 像素内容高度。"; return; }
            contentArea = new(area.X, area.Y + t, area.Width, area.Height - t - b);
            if (ValidateForTest is null)
            {
                // Enumerate beneath our own controller. Hiding a WPF modal window would end
                // ShowDialog and tear down the screenshot session before capture can start.
                await Task.Delay(50);
                if (closed) return;
                var center = new PixelPoint(contentArea.X + contentArea.Width / 2, contentArea.Y + contentArea.Height / 2);
                target = ResolveTargetForTest?.Invoke() ?? WindowDetectionService.Capture().FirstOrDefault(w => w.Bounds.Contains(center))?.Handle ?? IntPtr.Zero;
                NativeMethods.GetWindowRect(target, out targetBounds);
                if (target == IntPtr.Zero || (ResolveTargetForTest is null && OwnWindow(target))) { status.Text = "请重新框选其他应用的内容区。"; return; }
            }
            ShowFrame();
        }
        generation++; candidate = null; stableCount = 0; Running = true;
        status.Text = "请在内容区缓慢滚动；画面稳定后自动拼接。"; UpdateControls(); if (AutomaticPolling) timer.Start();
    }
    internal void Pause(string message = "已暂停")
    { Running = false; generation++; candidate = null; stableCount = 0; timer.Stop(); status.Text = message; UpdateControls(); }
    internal void Undo()
    {
        Pause("已暂停。请把页面滚回上一处，再继续。");
        if (Document?.Undo() == true) { preview.Source = Document.Thumbnail(); UpdateControls(); }
    }
    internal void Complete()
    {
        if (Document is null) return;
        Pause();
        try { Result = Document.Build(); Close(); } catch (Exception ex) { status.Text = "生成长图失败：" + ex.Message; }
    }
    private void UpdateControls()
    {
        start.Content = Running ? "暂停" : Document is null ? "开始" : "继续";
        direction.IsEnabled = top.IsEnabled = bottom.IsEnabled = Document is null && !Running;
        undo.IsEnabled = Document?.Appends > 0; finish.IsEnabled = Document is not null;
        dimensions.Text = Document is null ? $"选区 {contentArea.Width} × {contentArea.Height}" : $"{Document.Width} × {Document.Height} px · 已拼接 {Document.Appends} 次";
    }
    internal async Task PollAsync()
    {
        if (!Running || closed || inFlight) return;
        inFlight = true; int ticket = generation;
        try
        {
            if (!ValidateTarget()) { Pause(validationFailure + "；已暂停，可恢复后继续。"); return; }
            var next = await Task.Run(() => ScrollFrame.From(Capture(contentArea)));
            if (closed || ticket != generation) return;
            if (!ValidateTarget()) { Pause("窗口状态变化，当前帧已丢弃。"); return; }
            await AcceptFrameAsync(next, ticket);
        }
        catch (Exception ex) { if (!closed && ticket == generation) Pause("采集暂停：" + ex.Message); }
        finally { inFlight = false; }
    }
    internal async Task AcceptFrameAsync(ScrollFrame next, int? expectedGeneration = null)
    {
        int ticket = expectedGeneration ?? generation;
        if (!Running || closed) return;
        var previousCandidate = candidate;
        if (previousCandidate is null) { candidate = next; stableCount = 0; return; }
        bool stable = await Task.Run(() => ScrollMatcher.Stable(previousCandidate, next));
        if (closed || ticket != generation) return;
        candidate = next;
        if (!stable) { stableCount = 0; status.Text = "等待滚动画面稳定…"; return; }
        if (++stableCount < 2) return;
        if (Document is null)
        {
            Document = new(next, Direction); preview.Source = Document.Thumbnail(); status.Text = "首帧已记录，请开始缓慢滚动。"; UpdateControls(); return;
        }
        var baseline = Document.Last; var mode = Document.Direction;
        var match = await Task.Run(() => ScrollMatcher.Match(baseline, next, mode));
        if (closed || ticket != generation) return;
        if (match.Kind == ScrollMatchKind.Same) { status.Text = "未检测到新内容；可等待加载、继续滚动或完成。"; return; }
        if (match.Kind == ScrollMatchKind.Uncertain) { Pause("无法可靠拼接。可能滚动过快、反向、内容重复或有悬浮栏；请滚回上一处，或完成已有结果。"); return; }
        if (!Document.Append(next, match.Distance)) { Pause("已达到长图上限（30,000 像素高 / 3,200 万像素 / 1,000 次拼接）。请完成并分段截图。"); return; }
        preview.Source = Document.Thumbnail(); status.Text = $"已追加 {match.Distance} 像素；请继续缓慢滚动。"; UpdateControls();
    }
}
