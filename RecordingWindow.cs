using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cut.Core;
using Cut.Services;
using Microsoft.Win32;

namespace Cut;

internal sealed class RecordingWindow : Window
{
    private readonly PixelRect area, monitor;
    private readonly StackPanel settings = new();
    private readonly ComboBox fps = new() { Width = 100, Margin = new Thickness(4) };
    private readonly CheckBox cursor = new() { Content = "录制鼠标指针", IsChecked = true, Margin = new Thickness(4) };
    private readonly CheckBox countdown = new() { Content = "3 秒倒计时", IsChecked = true, Margin = new Thickness(4) };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Foreground = Brushes.DarkSlateGray };
    private readonly TextBlock clock = new() { Text = "00:00 / 01:00", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4) };
    private readonly Button toggle, stop;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? countdownCancel;
    private RecordingSession? session;
    private MediaTools? tools;
    private HotkeyService? stopKey;
    private Window? frame;
    private RecordingPreviewWindow? preview;
    private bool transition, closed, forceClose, excluded, stopRequested;
    private string stopHint = "F8";
    internal RecordingSession? SessionForTest => session;
    internal string MessageForTest => status.Text;
    internal string StopShortcutForTest => stopHint;
    internal bool CountdownEnabled { get => countdown.IsChecked == true; set => countdown.IsChecked = value; }
    internal Func<MediaTools, RecordingOptions, RecordingSession> CreateSession { get; set; } = (tools, options) => new(tools, options);
    internal Func<MediaTools, RecordingClip, RecordingPreviewWindow> CreatePreview { get; set; } = (tools, clip) => new(tools, clip);
    internal bool HasAnnotations { get; set; }

    internal RecordingWindow(PixelRect area, PixelRect monitor)
    {
        this.area = area; this.monitor = monitor;
        Title = "Cut · 选区录制"; Width = 440; Height = 290; MinWidth = 390; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = true; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(clock);
        settings.Children.Add(new TextBlock { Text = $"固定区域 {area.Width} × {area.Height} · 最长 60 秒 · 无声音", Margin = new Thickness(4) });
        var rates = new WrapPanel(); rates.Children.Add(new TextBlock { Text = "录制帧率", VerticalAlignment = VerticalAlignment.Center });
        foreach (int value in RecordingOptions.FrameRates) fps.Items.Add(value); fps.SelectedItem = 30;
        rates.Children.Add(fps); settings.Children.Add(rates);
        var checks = new WrapPanel(); checks.Children.Add(cursor); checks.Children.Add(countdown); settings.Children.Add(checks);
        root.Children.Add(settings); root.Children.Add(status);
        var actions = new WrapPanel();
        toggle = Add(actions, "开始录制", async () => await ToggleAsync());
        stop = Add(actions, "停止并预览", async () => await FinishAsync());
        Add(actions, "取消", Close); root.Children.Add(actions); Content = root;
        SourceInitialized += (_, _) =>
        {
            excluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && NativeMethods.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11);
            PositionOutside();
        };
        Loaded += (_, _) => { PositionOutside(); ReadyMessage(); UpdateControls(); timer.Start(); };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        LocationChanged += async (_, _) =>
        {
            if (!excluded && !transition && session?.State == RecordingState.Recording && OverlapsCapture())
                await PauseForOverlapAsync();
        };
        timer.Tick += async (_, _) =>
        {
            if (closed || transition || session is null) return;
            clock.Text = $"{TimeSpan.FromSeconds(session.Seconds):mm\\:ss} / 01:00";
            if (session.State != RecordingState.Recording) return;
            status.Text = $"● 录制中 · {stopHint} 停止 · {session.Backend}\n目标 {session.Options.Fps} FPS · 画面更新 {session.Progress.Fps:0.#} FPS · 缓存 {session.Cache.Bytes / 1048576d:0.#} MiB";
            if (!excluded && OverlapsCapture()) { await PauseForOverlapAsync(); return; }
            if (session.Seconds >= 60 || session.EncoderExited || session.Cache.Bytes >= RecordingCache.MaximumBytes) await FinishAsync();
        };
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (countdownCancel is not null) countdownCancel.Cancel();
            else if (!transition && session?.State == RecordingState.Recording) await ToggleAsync();
            else if (!transition) Close();
        };
        Closing += (_, e) =>
        {
            if (!forceClose && session is not null && (session.SegmentCount > 0 || session.State == RecordingState.Recording) &&
                MessageBox.Show(this, "放弃本次录制并关闭？", "Cut", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) { e.Cancel = true; return; }
            closed = true; lifetime.Cancel(); countdownCancel?.Cancel(); timer.Stop();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            preview?.Abort(); frame?.Close(); stopKey?.Dispose(); stopKey = null; session?.Dispose();
        };
    }
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionLock && e.Reason != SessionSwitchReason.RemoteDisconnect) return;
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (closed) return;
            countdownCancel?.Cancel();
            if (transition) { stopRequested = true; return; }
            if (session?.State != RecordingState.Recording) return;
            transition = true; UpdateControls();
            try { await session.PauseAsync(); if (!closed) status.Text = "桌面已锁定或会话断开，录制已暂停；返回桌面后可继续或完成。"; }
            catch (Exception ex) { if (!closed) status.Text = "采集已停止，已有录制保留：" + ex.Message; }
            finally { transition = false; if (!closed) UpdateControls(); }
        }));
    }
    private void ReadyMessage() => status.Text = HasAnnotations ? "将录制选区内的实时画面；截图标注不会带入。录完后可导出 GIF 或 MP4。" : "录制选区内的实时画面，录完后可导出 GIF 或 MP4。";
    private static Button Add(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => action(); parent.Children.Add(button); return button;
    }
    private void PositionOutside()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(Width * dpi.DpiScaleX), h = (int)Math.Ceiling(Height * dpi.DpiScaleY);
        int x = area.Right + 10, y = area.Y;
        if (x + w > monitor.Right)
        {
            if (area.X - w - 10 >= monitor.X) x = area.X - w - 10;
            else { x = area.X; y = area.Bottom + 10; if (y + h > monitor.Bottom) y = area.Y - h - 10; }
        }
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1),
            Math.Clamp(x, monitor.X, Math.Max(monitor.X, monitor.Right - w)), Math.Clamp(y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - h)), w, h, 0x0010);
    }
    private bool OverlapsCapture()
    {
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var bounds)) return true;
        return !area.Intersect(new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)).IsEmpty;
    }
    private async Task PauseForOverlapAsync()
    {
        transition = true; UpdateControls();
        try { await session!.PauseAsync(); status.Text = "当前系统无法排除录制控制条，已暂停；请将控制条移出选区后继续。"; }
        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally { transition = false; if (!closed) UpdateControls(); }
    }
    private void UpdateControls()
    {
        bool running = session?.State == RecordingState.Recording;
        settings.IsEnabled = !transition && session is null;
        toggle.Content = running ? "暂停" : session is null ? "开始录制" : "继续";
        toggle.IsEnabled = !transition && (session?.Seconds ?? 0) < 60;
        stop.IsEnabled = session is not null && (running || session.SegmentCount > 0) && !transition;
    }
    internal async Task ToggleAsync()
    {
        if (closed || transition) return;
        transition = true; UpdateControls();
        try
        {
            if (session?.State == RecordingState.Recording)
            { await session.PauseAsync(); status.Text = $"已暂停 · {stopHint} 停止并预览；暂停时间不会进入成片。"; return; }
            if (!excluded && OverlapsCapture()) throw new InvalidOperationException("当前系统无法排除控制条，请先将此窗口拖到选区外。");
            if (stopKey is null)
            {
                stopKey = new HotkeyService(); stopHint = "F8";
                if (!stopKey.TryRegister(0, 0x77))
                {
                    stopHint = "Ctrl + Alt + F8";
                    if (!stopKey.TryRegister(3, 0x77)) { stopKey.Dispose(); stopKey = null; throw new InvalidOperationException("F8 和 Ctrl + Alt + F8 均被占用，无法注册全局停止键。请释放其中一个快捷键后重试。"); }
                }
                stopKey.Pressed += async () => await FinishAsync();
            }
            tools ??= await MediaTools.LoadAsync(lifetime.Token);
            session ??= CreateSession(tools, new(area, monitor, (int)fps.SelectedItem, cursor.IsChecked == true));
            if (countdown.IsChecked == true)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); countdownCancel = cancel;
                try { for (int i = 3; i > 0; i--) { status.Text = $"{i} 秒后开始 · 将鼠标移入选区 · {stopHint} 停止"; await Task.Delay(1000, cancel.Token); } }
                finally { countdownCancel = null; }
            }
            if (stopRequested) { stopRequested = false; return; }
            ShowFrame(); status.Text = "正在准备实时采集…";
            await session.ResumeAsync(lifetime.Token);
            if (!closed)
            {
                settings.Visibility = Visibility.Collapsed; Height = 170; PositionOutside();
                status.Text = session.BackendNotice ?? $"● 录制中 · {stopHint} 停止 · {session.Backend}";
            }
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "倒计时已取消，可重新开始。"; }
        catch (Exception ex) { if (!closed) status.Text = ex.Message; }
        finally
        {
            if (!closed && session is not null && session.SegmentCount == 0 && session.State is RecordingState.Ready or RecordingState.Paused)
            {
                session.Dispose(); session = null; frame?.Close(); frame = null; stopKey?.Dispose(); stopKey = null;
            }
            transition = false; if (!closed) UpdateControls();
            if (!closed && stopRequested) { stopRequested = false; if (session?.State == RecordingState.Recording || session?.SegmentCount > 0) await FinishAsync(); }
        }
    }
    internal async Task FinishAsync()
    {
        if (closed) return;
        if (transition)
        {
            if (session?.State is not (RecordingState.Finishing or RecordingState.Finished)) { stopRequested = true; countdownCancel?.Cancel(); }
            return;
        }
        if (session is null) return;
        stopRequested = false; transition = true; UpdateControls();
        try
        {
            status.Text = "正在停止采集并准备预览…";
            var clip = await session.FinishAsync(new Progress<double>(p => { if (!closed) status.Text = $"正在准备预览 {p:P0}…"; }));
            if (closed) return;
            frame?.Close(); frame = null; stopKey?.Dispose(); stopKey = null;
            Topmost = false; WindowState = WindowState.Minimized;
            preview = CreatePreview(tools!, clip); preview.Owner = this;
            preview.ShowDialog();
            bool again = preview.RestartRequested; preview = null;
            if (closed) return;
            session.Dispose(); session = null;
            if (!again) { forceClose = true; Close(); return; }
            settings.Visibility = Visibility.Visible; Height = 290; WindowState = WindowState.Normal; Topmost = true;
            clock.Text = "00:00 / 01:00"; ReadyMessage(); PositionOutside(); Activate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) status.Text = "未能完成，已有录制保留，可重试：" + ex.Message; }
        finally { transition = false; if (!closed) UpdateControls(); }
    }
    private void ShowFrame()
    {
        if (frame is not null) return;
        frame = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, ResizeMode = ResizeMode.NoResize, IsHitTestVisible = false,
            Content = new Border { BorderThickness = new Thickness(2), BorderBrush = Brushes.OrangeRed } };
        frame.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(frame).Handle;
            NativeMethods.SetWindowLong(handle, -20, NativeMethods.GetWindowLong(handle, -20) | 0x20 | 0x80 | 0x08000000);
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) NativeMethods.SetWindowDisplayAffinity(handle, 0x11);
            NativeMethods.SetWindowPos(handle, new IntPtr(-1), area.X - 4, area.Y - 4, area.Width + 8, area.Height + 8, 0x0010);
        };
        frame.Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(frame);
            ((Border)frame.Content).BorderThickness = new Thickness(2 / dpi.DpiScaleX, 2 / dpi.DpiScaleY, 2 / dpi.DpiScaleX, 2 / dpi.DpiScaleY);
        };
        frame.Show();
    }
    internal void Abort() { forceClose = true; Close(); }
}
