using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cut.Core;
using Cut.Services;

namespace Cut;

internal sealed partial class ScrollCaptureWindow
{
    private readonly ComboBox captureMode = new() { Width = 140, Margin = new Thickness(4) };
    private readonly ComboBox speed = new() { Width = 170, Margin = new Thickness(4) };
    private AutoScrollPacer? pacer;
    private ScrollInputService? input;
    private HotkeyService? escape;
    private long armAt;
    private bool armed;
    private long lastStable;
    private long lastAutoPause = long.MinValue / 2;
    internal bool AutoScroll { get => captureMode.SelectedIndex == 1; set => captureMode.SelectedIndex = value ? 1 : 0; }
    internal ScrollSpeed Speed { get => (ScrollSpeed)speed.SelectedIndex; set => speed.SelectedIndex = (int)value; }
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;
    internal Func<int, bool> SendWheel { get; set; } = ScrollInputService.Wheel;
    internal bool SkipAutoGuardsForTest { get; set; }
    internal int CountdownMilliseconds { get; set; } = 2500;
    internal int InputsSent { get; private set; }

    private void AddAutoControls(Panel root)
    {
        captureMode.Items.Add("手动滚动"); captureMode.Items.Add("自动滚动"); captureMode.SelectedIndex = 0;
        speed.Items.Add("慢速 · 稳妥"); speed.Items.Add("标准 · 推荐"); speed.Items.Add("快速"); speed.SelectedIndex = 1;
        var row = new WrapPanel(); row.Children.Add(captureMode); row.Children.Add(speed); root.Children.Add(row);
        captureMode.SelectionChanged += (_, _) => { if (start is not null) UpdateControls(); };
        root.Children.Add(new TextBlock { Text = "自动模式：开始后将鼠标移入内容区。Esc、点击或手动滚动可暂停；暂停后可调整速度。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4), Foreground = System.Windows.Media.Brushes.DimGray });
    }
    private bool StartAuto()
    {
        StopAuto();
        if (!AutoScroll) return true;
        pacer = new(Speed); armAt = Now() + CountdownMilliseconds; armed = false; lastStable = Now();
        if (!SkipAutoGuardsForTest)
        {
            escape = new HotkeyService();
            escape.Pressed += () => Pause("已暂停自动滚动；预览已保留，可继续或完成。");
            if (!escape.TryRegister(0, 0x1B))
            { Pause("Esc 暂停键被占用，无法开始自动滚动；可改用手动模式。"); return false; }
            // Activation is attempted while the Start click still grants foreground permission.
            ScrollInputService.SetForegroundWindow(target);
        }
        status.Text = "即将开始自动滚动，请将鼠标移入内容区…（Esc 暂停）";
        return true;
    }
    private void StopAuto()
    {
        input?.Dispose(); input = null;
        escape?.Dispose(); escape = null;
        pacer = null; armed = false;
    }
    private bool AutoTargetValid()
    {
        if (SkipAutoGuardsForTest) return true;
        if (input?.Interrupted == true) return Reject("检测到手动操作");
        if (ScrollInputService.HasHeldInput()) return Reject("请先松开鼠标按键及 Ctrl / Shift / Alt / Win 键");
        var pointer = NativeMethods.CursorPosition();
        if (!contentArea.Contains(pointer)) return Reject("鼠标已离开内容区");
        var at = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(new() { X = pointer.X, Y = pointer.Y }), 2);
        if (at != target || NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), 2) != target)
            return Reject("目标窗口失去焦点或鼠标下方已被遮挡");
        return true;
    }
    private bool AutoReady()
    {
        if (pacer is null) return true;
        if (!armed)
        {
            if (Now() < armAt)
            { status.Text = $"{Math.Max(1, (int)Math.Ceiling((armAt - Now()) / 1000.0))} 秒后开始，将鼠标移入内容区…（Esc 暂停）"; return false; }
            if (!AutoTargetValid()) { Pause(validationFailure + "；已暂停，请恢复后继续。"); return false; }
            if (!SkipAutoGuardsForTest)
            {
                int ticket = generation;
                input = new ScrollInputService(() => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!closed && ticket == generation && Running) Pause("检测到手动操作，自动滚动已暂停；预览已保留。");
                })));
            }
            armed = true; lastStable = Now();
        }
        if (!AutoTargetValid()) { Pause(validationFailure + "；自动滚动已暂停。"); return false; }
        if (Now() - lastStable > 10000)
        { Pause("画面持续变化或滚动未稳定，已暂停；请等待加载或排除动态区域后继续。"); return false; }
        return true;
    }
    private async Task AutoStableAsync(int distance, int ticket)
    {
        var active = pacer;
        if (active is null || !armed || closed || !Running || ticket != generation) return;
        lastStable = Now();
        var action = active.Stable(Now(), distance, contentArea.Height);
        if (action == AutoScrollAction.Wait) return;
        if (action == AutoScrollAction.Stop)
        {
            // Stop injection before consulting an optional, potentially slow UIA provider.
            var pointer = SkipAutoGuardsForTest ? default : NativeMethods.CursorPosition();
            bool up = Document!.Direction == ScrollDirection.Up;
            Pause("未检测到新内容，已停止；预览已保留，可继续采集或完成。");
            int stoppedGeneration = generation;
            bool boundary = !SkipAutoGuardsForTest && await ScrollBoundaryService.AtBoundaryAsync(target, new Point(pointer.X, pointer.Y), up);
            if (!closed && !Running && generation == stoppedGeneration && boundary)
                status.Text = $"已到{(up ? "顶部" : "底部")}，自动滚动已停止；预览已保留，可继续采集或完成。";
            return;
        }
        if (ticket != generation || closed || !Running) return;
        if (!ValidateTarget() || !AutoTargetValid()) { Pause(validationFailure + "；已暂停。"); return; }
        int delta = (Document!.Direction == ScrollDirection.Up ? 1 : -1) * active.WheelMagnitude;
        if (!SendWheel(delta))
        { Pause("无法向目标窗口发送滚动输入，已暂停。请检查目标程序权限，或改用手动滚动。"); return; }
        active.InputSent(Now()); InputsSent++;
        candidate = null; stableCount = 0;
        status.Text = active.AttemptsWithoutProgress > 1 ? "等待新内容；连续无变化时将自动停止…（Esc 暂停）" : "正在自动滚动并拼接…（Esc 暂停）";
    }
}
