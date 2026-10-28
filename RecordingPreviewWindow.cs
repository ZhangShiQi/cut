using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cut.Services;
using Microsoft.Win32;

namespace Cut;

internal sealed class RecordingPreviewWindow : Window
{
    private readonly MediaTools tools;
    private readonly RecordingClip clip;
    private readonly MediaElement player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform };
    private readonly Slider start = new() { Minimum = 0, TickFrequency = .1, Margin = new Thickness(8, 2, 8, 2) };
    private readonly Slider end = new() { Minimum = 0, TickFrequency = .1, Margin = new Thickness(8, 2, 8, 2) };
    private readonly TextBlock range = new() { Margin = new Thickness(4), Foreground = Brushes.DimGray };
    private readonly TextBlock status = new() { Margin = new Thickness(4), TextWrapping = TextWrapping.Wrap, MaxHeight = 64 };
    private readonly ComboBox format = new() { Width = 110, Margin = new Thickness(4) };
    private readonly ComboBox size = new() { Width = 110, Margin = new Thickness(4) };
    private readonly ComboBox fps = new() { Width = 80, Margin = new Thickness(4) };
    private readonly ComboBox quality = new() { Width = 110, Margin = new Thickness(4) };
    private readonly CheckBox loop = new() { Content = "无限循环", IsChecked = true, Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button play = new() { Content = "暂停", Padding = new Thickness(14, 6, 14, 6) };
    private readonly Button save = new() { Content = "导出…" };
    private readonly Button cancel = new() { Content = "取消导出", Visibility = Visibility.Collapsed };
    private readonly Button restart = new() { Content = "重新录制" };
    private readonly StackPanel options = new();
    private readonly ProgressBar progress = new() { Minimum = 0, Maximum = 1, Height = 5, Margin = new Thickness(4), Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CancellationTokenSource? exportCancel;
    private bool playing, updating, busy, closed, forceClose, closeAfterExport, exported, presented;
    internal bool RestartRequested { get; private set; }
    internal Func<string?>? ChoosePathForTest { get; set; }
    internal RecordingExportOptions ExportOptions => new(format.SelectedIndex == 0 ? RecordingFormat.Gif : RecordingFormat.Mp4,
        (int)fps.SelectedItem, size.SelectedIndex switch { 1 => .75, 2 => .5, _ => 1 }, start.Value, end.Value, loop.IsChecked == true, quality.SelectedIndex == 1);

    internal RecordingPreviewWindow(MediaTools tools, RecordingClip clip)
    {
        this.tools = tools; this.clip = clip;
        Title = "Cut · 录制预览"; Width = 900; Height = 720; MinWidth = 680; MinHeight = 550;
        ShowActivated = true; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = new SolidColorBrush(Color.FromRgb(245, 247, 251));
        var root = new DockPanel { Margin = new Thickness(14) };
        var heading = new TextBlock { Text = $"录制预览  ·  {clip.Options.Area.Width} × {clip.Options.Area.Height}  ·  {clip.Seconds:0.00} 秒  ·  {clip.Options.Fps} FPS",
            FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 4, 10) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var transport = new WrapPanel(); transport.Children.Add(play); transport.Children.Add(range); bottom.Children.Add(transport);
        bottom.Children.Add(TrimRow("起点", start)); bottom.Children.Add(TrimRow("终点", end));
        var output = new WrapPanel();
        format.Items.Add("GIF 动图"); format.Items.Add("MP4 视频"); format.SelectedIndex = 0;
        size.Items.Add("原尺寸"); size.Items.Add("75%"); size.Items.Add("50%"); size.SelectedIndex = 0;
        foreach (int value in RecordingOptions.FrameRates.Where(f => f <= clip.Options.Fps)) fps.Items.Add(value);
        fps.SelectedItem = Math.Min(15, clip.Options.Fps);
        quality.Items.Add("标准画质"); quality.Items.Add("高画质"); quality.SelectedIndex = 1;
        AddOption(output, "格式", format); AddOption(output, "尺寸", size); AddOption(output, "FPS", fps); AddOption(output, "画质", quality); output.Children.Add(loop);
        options.Children.Add(output); bottom.Children.Add(options); bottom.Children.Add(status); bottom.Children.Add(progress);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(restart); actions.Children.Add(cancel); actions.Children.Add(save); bottom.Children.Add(actions);
        root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(21, 27, 36)), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Child = player, Margin = new Thickness(0, 0, 0, 8) });
        Content = root;
        start.Maximum = end.Maximum = clip.Seconds; end.Value = clip.Seconds;
        player.Source = new Uri(clip.PreviewPath);
        player.MediaOpened += (_, _) => { player.Position = TimeSpan.Zero; player.Play(); playing = true; UpdatePlay(); };
        player.MediaEnded += (_, _) => { player.Position = TimeSpan.FromSeconds(start.Value); if (playing) player.Play(); };
        player.MediaFailed += (_, e) => { status.Text = "系统播放器无法预览此视频，仍可导出 GIF 或 MP4：" + e.ErrorException.Message; playing = false; UpdatePlay(); };
        play.Click += (_, _) => { if (playing) player.Pause(); else player.Play(); playing = !playing; UpdatePlay(); };
        start.ValueChanged += (_, _) => ChangeRange(true);
        end.ValueChanged += (_, _) => ChangeRange(false);
        format.SelectionChanged += (_, _) =>
        {
            loop.Visibility = format.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            fps.SelectedItem = format.SelectedIndex == 0 ? Math.Min(15, clip.Options.Fps) : clip.Options.Fps;
            ExportNotice();
        };
        fps.SelectionChanged += (_, _) => ExportNotice();
        size.SelectionChanged += (_, _) => ExportNotice();
        save.Click += async (_, _) => await ExportAsync();
        cancel.Click += (_, _) => exportCancel?.Cancel();
        restart.Click += (_, _) => { if (!busy && ConfirmDiscard()) { RestartRequested = true; forceClose = true; Close(); } };
        timer.Tick += (_, _) =>
        {
            if (closed) return;
            if (playing && player.Position.TotalSeconds >= end.Value) { player.Position = TimeSpan.FromSeconds(start.Value); player.Play(); }
            UpdateRange();
        };
        Loaded += (_, _) => { player.Position = TimeSpan.FromSeconds(start.Value); UpdateRange(); ExportNotice(); player.Play(); playing = true; UpdatePlay(); timer.Start(); };
        ContentRendered += (_, _) =>
        {
            if (presented || closed) return;
            presented = true;
            // Raise the completed preview once, including stops from the global
            // shortcut, then restore normal stacking so it does not stay pinned.
            Activate(); Focus(); Topmost = false;
        };
        Closing += (_, e) =>
        {
            if (!forceClose && !ConfirmDiscard()) { e.Cancel = true; return; }
            if (busy && !forceClose) { closeAfterExport = true; exportCancel?.Cancel(); e.Cancel = true; }
        };
        Closed += (_, _) => { closed = true; exportCancel?.Cancel(); timer.Stop(); player.Stop(); player.Close(); player.Source = null; };
    }
    private static FrameworkElement TrimRow(string label, Slider slider)
    {
        var row = new DockPanel(); var text = new TextBlock { Text = label, Width = 38, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left); row.Children.Add(text); row.Children.Add(slider); return row;
    }
    private static void AddOption(Panel parent, string label, Control control)
    { parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(4), VerticalAlignment = VerticalAlignment.Center }); parent.Children.Add(control); }
    private bool ConfirmDiscard() => exported || MessageBox.Show(this, busy ? "取消当前导出并关闭预览？尚未保存的录制将丢弃。" : "尚未导出，放弃本次录制？", "Cut", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    private void UpdatePlay() => play.Content = playing ? "暂停" : "播放";
    private void ChangeRange(bool fromStart)
    {
        if (updating) return; updating = true;
        double minimum = Math.Min(clip.Seconds, 1.0 / clip.Options.Fps);
        if (fromStart) start.Value = Math.Min(start.Value, end.Value - minimum);
        else end.Value = Math.Max(end.Value, start.Value + minimum);
        player.Position = TimeSpan.FromSeconds(fromStart ? start.Value : Math.Max(start.Value, end.Value - minimum));
        UpdateRange(); updating = false;
    }
    private void UpdateRange() => range.Text = $"{player.Position.TotalSeconds:0.00}s   ·   保留 {start.Value:0.00}–{end.Value:0.00}s（{end.Value - start.Value:0.00}s）";
    private void ExportNotice()
    {
        if (busy || fps.SelectedItem is null) return;
        bool gif = format.SelectedIndex == 0;
        status.Text = gif && (int)fps.SelectedItem >= 30 ? "GIF 高帧率时序会近似取整，文件可能较大；流畅动画建议导出 MP4。" :
            gif ? "GIF 默认无限循环；实际文件大小在导出完成后显示。可以继续导出另一种格式。" :
            "MP4 使用 H.264，无声音；奇数宽高会在右侧或底部补 1 像素黑边，以兼容视频播放器。";
    }
    internal void SetTrimForTest(double begin, double finish) { updating = true; start.Value = begin; end.Value = finish; updating = false; ChangeRange(true); }
    internal void SetFormatForTest(RecordingFormat value) => format.SelectedIndex = value == RecordingFormat.Gif ? 0 : 1;
    internal async Task ExportAsync()
    {
        if (busy || closed) return;
        var settings = ExportOptions;
        try { settings.Validate(clip); } catch (Exception ex) { status.Text = ex.Message; return; }
        string extension = settings.Format == RecordingFormat.Gif ? ".gif" : ".mp4";
        string? path;
        if (ChoosePathForTest is not null) path = ChoosePathForTest();
        else
        {
            var dialog = new SaveFileDialog { Title = "导出录制", FileName = "Cut-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + extension,
                DefaultExt = extension, Filter = settings.Format == RecordingFormat.Gif ? "GIF 动图|*.gif" : "MP4 视频|*.mp4", AddExtension = true, OverwritePrompt = true };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (path is null) return;
        busy = true; options.IsEnabled = start.IsEnabled = end.IsEnabled = save.IsEnabled = restart.IsEnabled = false;
        progress.Visibility = cancel.Visibility = Visibility.Visible; progress.Value = 0;
        using var cancelSource = new CancellationTokenSource(); exportCancel = cancelSource;
        try
        {
            player.Pause(); playing = false; UpdatePlay(); status.Text = "正在导出…";
            await RecordingExporter.ExportAsync(tools, clip, settings, path, new Progress<double>(p => { if (!closed) { progress.Value = p; status.Text = $"正在导出 {p:P0}…"; } }), cancelSource.Token);
            exported = true;
            if (!closed) status.Text = $"已保存 {Path.GetFileName(path)} · {new FileInfo(path).Length / 1048576d:0.##} MiB；可继续导出其他格式。";
        }
        catch (OperationCanceledException) { if (!closed) status.Text = "已取消导出，录制内容保留，可重新导出。"; }
        catch (Exception ex) { if (!closed) status.Text = "导出失败，录制内容保留：" + ex.Message; }
        finally
        {
            exportCancel = null; busy = false;
            if (!closed)
            {
                options.IsEnabled = start.IsEnabled = end.IsEnabled = save.IsEnabled = restart.IsEnabled = true;
                progress.Visibility = cancel.Visibility = Visibility.Collapsed;
                if (closeAfterExport) { forceClose = true; Close(); }
            }
        }
    }
    internal void Abort() { forceClose = true; exportCancel?.Cancel(); Close(); }
}
