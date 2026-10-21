using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cut.Services;

namespace Cut;

public partial class QrResultWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, CancellationToken, Task> copyText;
    private readonly Action<string> openLink;
    private bool busy;
    public bool ActionCompleted { get; private set; }
    internal QrCodeResult? SelectedResult => ResultList.SelectedItem as QrCodeResult;
    public QrResultWindow(IReadOnlyList<QrCodeResult> results)
        : this(results, ExportService.CopyTextAsync, LinkService.Open) { }
    internal QrResultWindow(IReadOnlyList<QrCodeResult> results, Func<string, CancellationToken, Task> copyText, Action<string> openLink)
    {
        InitializeComponent();
        this.copyText = copyText;
        this.openLink = openLink;
        SummaryText.Text = $"识别到 {results.Count} 项内容 · 相同内容已合并";
        ResultList.ItemsSource = results;
        if (results.Count > 0) ResultList.SelectedIndex = 0;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => lifetime.Cancel();
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ContentText.Text = SelectedResult?.Text ?? "";
        OpenButton.Visibility = SelectedResult?.WebUri is not null ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = SelectedResult is not null && !busy;
        StatusText.Text = "关闭此窗口可返回截图选区。";
    }
    private void BackClick(object sender, RoutedEventArgs e) => Close();
    private async void CopyClick(object sender, RoutedEventArgs e) => await CopySelectedAsync();
    internal async Task CopySelectedAsync()
    {
        if (busy || SelectedResult is not { } result) return;
        SetBusy(true);
        try
        {
            await copyText(result.Text, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            ActionCompleted = true;
            Close();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = "复制失败，请重试。" + ex.Message; }
        finally { SetBusy(false); }
    }
    private void OpenClick(object sender, RoutedEventArgs e) => OpenSelected();
    internal void OpenSelected()
    {
        if (busy || SelectedResult is not { WebUri: not null } result) return;
        try { openLink(result.Text); ActionCompleted = true; Close(); }
        catch (Exception ex) { StatusText.Text = "无法打开链接，可复制后手动打开。" + ex.Message; }
    }
    private void SetBusy(bool value)
    {
        busy = value;
        ResultList.IsEnabled = !value;
        CopyButton.IsEnabled = !value && SelectedResult is not null;
        OpenButton.IsEnabled = !value;
    }
    internal void SelectResult(int index) => ResultList.SelectedIndex = index;
}
