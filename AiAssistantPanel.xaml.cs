using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cut.Services;

namespace Cut;

public partial class AiAssistantPanel : UserControl
{
    private IAiService service = new CompatibleAiService();
    private Func<AiSettings> loadSettings = new AiSettingsStore().Load;
    private Func<bool, string> source = _ => "";
    private Func<string, CancellationToken, Task> copy = ExportService.CopyTextAsync;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? generation;
    private long version;
    private bool ready, applying, copying, stale;
    public event Action? Completed;
    public AiAssistantPanel()
    {
        InitializeComponent(); ready = true; Refresh();
        AnswerText.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            async (_, e) => { e.Handled = true; await CopyAsync(true); },
            (_, e) => { e.CanExecute = !copying && AnswerText.SelectionLength > 0; e.Handled = true; }));
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (AnswerText.IsKeyboardFocusWithin) await CopyAsync(false);
                else await GenerateAsync();
            }
        };
    }
    internal void Configure(Func<bool, string> getSource, Func<string, CancellationToken, Task> copyText,
        IAiService? aiService = null, Func<AiSettings>? settings = null)
    { source = getSource; copy = copyText; service = aiService ?? service; loadSettings = settings ?? loadSettings; }
    internal void SourceChanged() => Invalidate("原文已修改，回答待更新。");
    internal void SourceSelectionChanged()
    {
        if (ready && ScopeBox.SelectedIndex == 1) Invalidate("选中文字已变化，回答待更新。");
    }
    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        QuestionText.Visibility = ModeBox.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        Invalidate("任务或范围已变化，点击生成更新回答。");
    }
    private void QuestionChanged(object sender, TextChangedEventArgs e)
    { if (ready) Invalidate("问题已修改，回答待更新。"); }
    private void Invalidate(string message)
    {
        Stop();
        if (AnswerText.Text.Length > 0) stale = true;
        StateText.Text = message; Refresh();
    }
    private void AnswerChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        if (!applying) { Stop(); StateText.Text = stale ? "回答已编辑；原文或任务已变化，请核对。" : "回答已手动编辑。"; }
        Refresh();
    }
    private void AnswerSelectionChanged(object sender, RoutedEventArgs e) { if (ready) Refresh(); }
    private async void GenerateClick(object sender, RoutedEventArgs e) => await GenerateAsync();
    private void StopClick(object sender, RoutedEventArgs e) => Stop("已停止，当前内容已保留；回答可能不完整。");
    internal void Stop(string? message = null)
    {
        var pending = generation; generation = null; version++;
        pending?.Cancel();
        if (message is not null) StateText.Text = message;
        Refresh();
    }
    internal void ClosePanel() { lifetime.Cancel(); Stop(); }
    internal async Task GenerateAsync()
    {
        if (copying || lifetime.IsCancellationRequested || generation is not null) return;
        bool selected = ScopeBox.SelectedIndex == 1;
        string text = source(selected);
        if (string.IsNullOrWhiteSpace(text)) { StateText.Text = selected ? "请先在左侧选中文字。" : "请先提供原文。"; return; }
        if (ModeBox.SelectedIndex == 4 && string.IsNullOrWhiteSpace(QuestionText.Text)) { StateText.Text = "请输入自定义问题。"; QuestionText.Focus(); return; }
        using var current = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        generation = current; long requestVersion = ++version; bool received = false;
        StateText.Text = selected ? "正在分析选中文字…" : "正在分析全部原文…"; Refresh();
        try
        {
            var request = new AiRequest(text, (AiTask)ModeBox.SelectedIndex, QuestionText.Text);
            var config = loadSettings();
            var result = await service.GenerateAsync(request, config, chunk => Dispatcher.InvokeAsync(() =>
            {
                if (current.IsCancellationRequested || version != requestVersion || lifetime.IsCancellationRequested) return;
                applying = true;
                try
                {
                    if (!received) { AnswerText.Clear(); received = true; stale = false; }
                    AnswerText.AppendText(chunk);
                    AnswerText.ScrollToEnd();
                }
                finally { applying = false; }
            }).Task, current.Token);
            if (current.IsCancellationRequested || version != requestVersion || lifetime.IsCancellationRequested) return;
            StateText.Text = result.Truncated ? "回答达到模型输出上限，内容可能不完整。" : (selected ? "选中文字" : "全部原文") + " · 生成完成，可编辑和复制。";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested) { }
        catch (AiException ex) { if (version == requestVersion) StateText.Text = ex.Message + (received ? " 已有部分回答已保留。" : " 已有回答已保留。"); }
        catch { if (version == requestVersion) StateText.Text = "生成失败，请检查模型设置后重试。已有内容已保留。"; }
        finally { if (ReferenceEquals(generation, current)) generation = null; Refresh(); }
    }
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsWindow { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.WasSaved) Invalidate("模型设置已保存，点击生成使用新配置。");
    }
    private async void CopySelectedClick(object sender, RoutedEventArgs e) => await CopyAsync(true);
    private async void CopyAllClick(object sender, RoutedEventArgs e) => await CopyAsync(false);
    internal async Task CopyAsync(bool selectedOnly)
    {
        string text = selectedOnly ? AnswerText.SelectedText : AnswerText.Text;
        if (copying || lifetime.IsCancellationRequested || text.Length == 0) return;
        Stop(); copying = true; AnswerText.IsReadOnly = true; Refresh();
        try
        {
            await copy(text, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (selectedOnly) StateText.Text = "已复制选中的回答。";
            else Completed?.Invoke();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { StateText.Text = "复制失败，请重试。编辑内容已保留。"; }
        finally { copying = false; Refresh(); }
    }
    private void Refresh()
    {
        if (!ready) return;
        GenerateButton.IsEnabled = !copying && generation is null && !lifetime.IsCancellationRequested;
        GenerateButton.Content = AnswerText.Text.Length == 0 ? "生成回答" : "重新生成";
        StopButton.IsEnabled = generation is not null;
        SettingsButton.IsEnabled = !copying && generation is null;
        CopySelectedButton.IsEnabled = !copying && AnswerText.SelectionLength > 0;
        CopyAllButton.IsEnabled = !copying && AnswerText.Text.Length > 0;
        AnswerText.IsReadOnly = copying || generation is not null;
    }
    internal string Answer { get => AnswerText.Text; set => AnswerText.Text = value; }
    internal string Status => StateText.Text;
    internal bool IsGenerating => generation is not null;
    internal bool IsStale => stale;
    internal void SelectAnswer(int start, int length) => AnswerText.Select(start, length);
    internal void CopyCommand() => ApplicationCommands.Copy.Execute(null, AnswerText);
    internal void SetTask(AiTask task, bool selected = false, string question = "")
    { ModeBox.SelectedIndex = (int)task; ScopeBox.SelectedIndex = selected ? 1 : 0; QuestionText.Text = question; }
}
