using System;
using System.Linq;
using System.Threading;
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

internal sealed partial class ScrollCaptureWindow : Window
{
    private readonly PixelRect area, monitor;
    private PixelRect contentArea;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ComboBox direction = new() { Width = 170, Margin = new Thickness(4) };
    private readonly TextBox top = new() { Text = "0", Width = 50, Margin = new Thickness(4) }, bottom = new() { Text = "0", Width = 50, Margin = new Thickness(4) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8), Foreground = Brushes.DarkSlateGray };
    private readonly TextBlock dimensions = new() { Margin = new Thickness(8), FontWeight = FontWeights.SemiBold };
    private readonly TextBlock layerNotice = new() { Margin = new Thickness(8, 0, 8, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Visibility = Visibility.Collapsed,
        Text = "页面含固定背景，拼接后背景可能出现接缝；完成后请检查预览。" };
    private readonly Image preview = new() { Height = 130, Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly Button start, undo, finish;
    private Window? frame;
    private bool closed, inFlight, excluded;
    private int generation, stableCount;
    private long? uncertainSince;
    private ScrollFrame? candidate;
    private IntPtr target;
    private NativeMethods.Rect targetBounds;
    private string validationFailure = "目标窗口状态变化";
    private readonly ScrollMatchingSession matcher = new();
    private readonly CancellationTokenSource lifetime = new();
    private bool finishing;
    private Task processing = Task.CompletedTask;
    internal bool Running { get; private set; }
    internal ScrollCaptureDocument? Document { get; private set; }
    internal BitmapSource? Result { get; private set; }
    internal string Message => status.Text;
    internal Func<PixelRect, BitmapSource> Capture { get; set; } = CaptureService.CaptureRegion;
    internal Func<bool>? ValidateForTest { get; set; }
    internal Func<IntPtr>? ResolveTargetForTest { get; set; }
    internal bool AutomaticPolling { get; set; } = true;
    internal Func<ScrollFrame, ScrollFrame, ScrollDirection, ScrollMatch>? MatchForTest { get; set; }
    internal int MatchRetryMilliseconds { get; set; } = 1500;
    internal ScrollDirection Direction { get => direction.SelectedIndex == 1 ? ScrollDirection.Up : ScrollDirection.Down; set => direction.SelectedIndex = value == ScrollDirection.Up ? 1 : 0; }

    public ScrollCaptureWindow(PixelRect area, PixelRect monitor)
    {
        this.area = contentArea = area; this.monitor = monitor;
        Title = "Cut · 长截图"; Width = 390; Height = 550; MinWidth = 340; MinHeight = 360;
        Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock { Text = "长截图 · 自动拼接", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 8) });
        direction.Items.Add("向下 · 网页"); direction.Items.Add("向上 · 聊天历史"); direction.SelectedIndex = 0;
        var options = new WrapPanel(); options.Children.Add(direction);
        root.Children.Add(options);
        AddAutoControls(root);
        var trims = new WrapPanel();
        trims.Children.Add(new TextBlock { Text = "排除顶部 / 底部（像素）", VerticalAlignment = VerticalAlignment.Center });
        trims.Children.Add(top); trims.Children.Add(bottom); root.Children.Add(trims);
        var actions = new WrapPanel();
        start = Add(actions, "开始", async () =>
        {
            // A mouse hook can pause before WPF delivers the same click to this button.
            // Do not interpret that single pause click as a request to resume immediately.
            if (!Running && Now() - lastAutoPause < 600) return;
            try { await ToggleAsync(); } catch (Exception ex) { Pause("无法开始采集：" + ex.Message); }
        });
        undo = Add(actions, "撤销拼接", async () => await UndoAsync());
        finish = Add(actions, "完成", async () => await CompleteAsync());
        Add(actions, "取消", Close); root.Children.Add(actions);
        root.Children.Add(dimensions); root.Children.Add(status); root.Children.Add(layerNotice);
        root.Children.Add(new TextBlock { Text = "最近采集原图 · 完成后生成长图", Margin = new Thickness(8, 4, 8, 0), Foreground = Brushes.DimGray });
        root.Children.Add(preview);
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
        Closed += (_, _) =>
        {
            closed = true; Running = false; generation++; lifetime.Cancel(); timer.Stop(); StopAuto(); frame?.Close();
            // Do not wait on disk or final composition on the dispatcher.
            _ = Task.Run(() => Document?.Dispose());
        };
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
        if (closed || finishing) return;
        if (Running) { Pause("已暂停。继续前请勿改变窗口位置或缩放。"); return; }
        if (!processing.IsCompleted) { status.Text = "上一帧仍在处理，请稍后继续。"; return; }
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
        generation++; candidate = null; stableCount = 0; uncertainSince = null; Running = true;
        status.Text = "请在内容区缓慢滚动；画面稳定后自动拼接。";
        if (!StartAuto()) return;
        UpdateControls(); if (AutomaticPolling) timer.Start();
    }
    internal void Pause(string message = "已暂停")
    {
        if (Running && pacer is not null) lastAutoPause = Now();
        Running = false; generation++; candidate = null; stableCount = 0; uncertainSince = null; timer.Stop(); StopAuto(); status.Text = message; UpdateControls();
    }
    internal async Task UndoAsync()
    {
        if (finishing || closed) return;
        Pause("已暂停。请把页面滚回上一处，再继续。");
        finishing = true; UpdateControls();
        try
        {
            await DrainWorkerAsync();
            if (!closed && Document is not null && await Task.Run(Document.Undo))
            { preview.Source = Document.Thumbnail(); UpdateControls(); }
        }
        catch (Exception ex) { if (!closed) status.Text = "撤销失败：" + ex.Message; }
        finally { finishing = false; if (!closed) UpdateControls(); }
    }
    internal async Task CompleteAsync()
    {
        if (Document is null || finishing || closed) return;
        Pause();
        finishing = true; UpdateControls(); status.Text = "正在从原始截图统一合成长图…";
        try
        {
            await DrainWorkerAsync();
            if (closed) return;
            var result = await Task.Run(() => Document.Build(lifetime.Token));
            if (!closed) { Result = result; Close(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) status.Text = "生成长图失败，原图缓存已保留，可重试：" + ex.Message; }
        finally { finishing = false; if (!closed) UpdateControls(); }
    }
    private async Task DrainWorkerAsync()
    {
        // PollAsync reports the failure and pauses. A failed uncommitted frame must
        // not prevent completing/undoing the already accepted cache afterward.
        try { await processing; } catch { }
    }
    private void UpdateControls()
    {
        start.Content = Running ? "暂停" : Document is null ? "开始" : "继续";
        start.IsEnabled = !finishing;
        direction.IsEnabled = top.IsEnabled = bottom.IsEnabled = Document is null && !Running;
        captureMode.IsEnabled = !Running && !finishing; speed.IsEnabled = !Running && !finishing && AutoScroll;
        undo.IsEnabled = !finishing && Document?.Appends > 0; finish.IsEnabled = !finishing && Document is not null;
        dimensions.Text = Document is null ? $"选区 {contentArea.Width} × {contentArea.Height}" : $"预计 {Document.Width} × {Document.Height} px · 已缓存 {Document.Appends + 1} 张原图";
    }
    internal async Task PollAsync()
    {
        if (!Running || closed || inFlight || !processing.IsCompleted) return;
        inFlight = true; int ticket = generation;
        try
        {
            if (!AutoReady()) return;
            if (!ValidateTarget()) { Pause(validationFailure + "；已暂停，可恢复后继续。"); return; }
            var next = await Task.Run(() => ScrollFrame.From(Capture(contentArea)));
            if (closed || ticket != generation) return;
            if (!ValidateTarget()) { Pause("窗口状态变化，当前帧已丢弃。"); return; }
            processing = AcceptFrameAsync(next, ticket);
            // Timer acquisition and the worker have independent lifetimes. There is
            // only one in-flight pair; no unbounded queue or speculative wheel input.
            await processing;
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
            var initialDirection = Direction;
            var document = await Task.Run(() => new ScrollCaptureDocument(next, initialDirection));
            if (closed || ticket != generation) { document.Dispose(); return; }
            Document = document; preview.Source = Document.Thumbnail(); status.Text = "首帧原图已缓存。"; UpdateControls(); await AutoStableAsync(0, ticket); return;
        }
        var baseline = Document.Last; var mode = Document.Direction;
        var match = await Task.Run(() => (MatchForTest ?? matcher.Match)(baseline, next, mode));
        if (closed || ticket != generation) return;
        if (match.Kind == ScrollMatchKind.Uncertain)
        {
            uncertainSince ??= Now();
            if (Now() - uncertainSince.Value < MatchRetryMilliseconds)
            { status.Text = "正在等待图片加载或悬停效果稳定；暂不继续滚动…"; return; }
            Pause("无法可靠拼接。" + (match.Hint ?? "重叠内容不足或匹配存在歧义，请滚回上一处后继续，或完成已有结果") + "。"); return;
        }
        uncertainSince = null;
        if (match.Kind == ScrollMatchKind.Same) { status.Text = "未检测到新内容；可等待加载、继续滚动或完成。"; await AutoStableAsync(0, ticket); return; }
        if (!Document.CanAppend(match.Distance)) { Pause("已达到长图上限（30,000 像素高 / 3,200 万像素 / 1,000 次拼接）。请完成并分段截图。"); return; }
        long cached = await Task.Run(() => Document.Cache(next));
        if (closed || ticket != generation) return;
        Document.Commit(next, match.Distance, cached);
        if (match.Layered) layerNotice.Visibility = Visibility.Visible;
        preview.Source = Document.Thumbnail(); status.Text = $"原图已缓存，确认新增 {match.Distance} 像素；完成后统一合成。"; UpdateControls();
        await AutoStableAsync(match.Distance, ticket);
    }
}
