using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Cut.Services;

namespace Cut;

public partial class OcrResultWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<string, CancellationToken, Task> copyText;
    private bool busy;
    public bool ActionCompleted { get; private set; }
    public OcrResultWindow(BitmapSource image, OcrResult result) : this(image, result, ExportService.CopyTextAsync) { }
    internal OcrResultWindow(BitmapSource image, OcrResult result, Func<string, CancellationToken, Task> copyText)
    {
        InitializeComponent();
        this.copyText = copyText;
        PreviewImage.Source = image;
        SummaryText.Text = $"识别到 {result.Lines.Count} 个文本区域 · 中英文混排 · 本地离线识别";
        ContentText.Text = result.Text;
        ContentText.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, async (_, e) => { e.Handled = true; await CopyAsync(true); },
            (_, e) => { e.CanExecute = !busy && ContentText.SelectionLength > 0; e.Handled = true; }));
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; await CopyAsync(false); }
        };
        Loaded += (_, _) => { ContentText.Focus(); ContentText.CaretIndex = 0; };
        Closed += (_, _) => lifetime.Cancel();
        RefreshButtons();
    }
    private void SelectionChanged(object sender, RoutedEventArgs e) => RefreshButtons();
    private void TextChanged(object sender, TextChangedEventArgs e) => RefreshButtons();
    private void BackClick(object sender, RoutedEventArgs e) => Close();
    private async void CopySelectionClick(object sender, RoutedEventArgs e) => await CopyAsync(true);
    private async void CopyAllClick(object sender, RoutedEventArgs e) => await CopyAsync(false);
    private void MergeClick(object sender, RoutedEventArgs e) => MergeLines();
    internal void MergeLines()
    {
        if (busy) return;
        string merged = Regex.Replace(ContentText.Text, @"[ \t]*\r?\n[ \t]*", " ");
        ContentText.BeginChange();
        ContentText.SelectAll();
        ContentText.SelectedText = merged;
        ContentText.EndChange();
        ContentText.CaretIndex = ContentText.Text.Length;
        StatusText.Text = "已合并换行，可按 Ctrl + Z 撤销。";
    }
    internal async Task CopyAsync(bool selectedOnly)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        string text = selectedOnly ? ContentText.SelectedText : ContentText.Text;
        if (text.Length == 0) return;
        busy = true;
        ContentText.IsReadOnly = true;
        RefreshButtons();
        try
        {
            await copyText(text, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (selectedOnly) StatusText.Text = "已复制选中文字，可继续编辑。";
            else { ActionCompleted = true; Close(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { StatusText.Text = "复制失败，请重试。" + ex.Message; }
        finally { busy = false; ContentText.IsReadOnly = false; RefreshButtons(); }
    }
    private void RefreshButtons()
    {
        if (CopyAllButton is null) return;
        CopyAllButton.IsEnabled = !busy && ContentText.Text.Length > 0;
        CopySelectionButton.IsEnabled = !busy && ContentText.SelectionLength > 0;
        MergeButton.IsEnabled = !busy && ContentText.Text.Contains('\n');
    }
    internal string EditedText { get => ContentText.Text; set => ContentText.Text = value; }
    internal void SelectText(int start, int length) => ContentText.Select(start, length);
    internal void UndoEdit() => ContentText.Undo();
    internal void CopySelectionCommand() => ApplicationCommands.Copy.Execute(null, ContentText);
}
