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
    private readonly ITranslationService translationService;
    private readonly Func<TranslationSettings> loadTranslationSettings;
    private CancellationTokenSource? translation;
    private long translationVersion;
    private bool busy, initialized, applyingTranslation, staleTranslation;
    public bool ActionCompleted { get; private set; }
    public OcrResultWindow(BitmapSource image, OcrResult result) : this(image, result, ExportService.CopyTextAsync) { }
    internal OcrResultWindow(BitmapSource image, OcrResult result, Func<string, CancellationToken, Task> copyText,
        ITranslationService? translationService = null, Func<TranslationSettings>? loadTranslationSettings = null)
    {
        InitializeComponent();
        this.copyText = copyText;
        this.translationService = translationService ?? new AzureTranslationService();
        this.loadTranslationSettings = loadTranslationSettings ?? new TranslationSettingsStore().Load;
        PreviewImage.Source = image;
        SummaryText.Text = $"识别到 {result.Lines.Count} 个文本区域 · OCR 本地离线识别 · 原文与译文均可编辑";
        ContentText.Text = result.Text;
        BindCopy(ContentText, false); BindCopy(TranslationText, true);
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (translation is not null) CancelTranslation("已取消翻译，原文和已有译文已保留。");
                else Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            { e.Handled = true; await CopyAsync(false, TranslationText.IsKeyboardFocusWithin); }
        };
        Loaded += (_, _) => { ContentText.Focus(); ContentText.CaretIndex = 0; };
        Closed += (_, _) => { lifetime.Cancel(); CancelTranslation(); };
        initialized = true;
        RefreshButtons();
    }
    private void BindCopy(TextBox box, bool translated) => box.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
        async (_, e) => { e.Handled = true; await CopyAsync(true, translated); },
        (_, e) => { e.CanExecute = !busy && box.SelectionLength > 0; e.Handled = true; }));
    private void SelectionChanged(object sender, RoutedEventArgs e) => RefreshButtons();
    private void TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        InvalidateTranslation("原文已修改，译文待更新。");
        RefreshButtons();
    }
    private void TranslationTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        if (!applyingTranslation)
        {
            CancelTranslation("已取消翻译，保留你编辑的译文。");
            if (!staleTranslation) TranslationStateText.Text = "译文已手动编辑。";
        }
        RefreshButtons();
    }
    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) InvalidateTranslation("语言选项已修改，译文待更新。");
    }
    private void InvalidateTranslation(string message)
    {
        CancelTranslation(message);
        if (TranslationText.Text.Length > 0) { staleTranslation = true; TranslationStateText.Text = message; }
    }
    private void BackClick(object sender, RoutedEventArgs e) => Close();
    private async void CopySelectionClick(object sender, RoutedEventArgs e) => await CopyAsync(true);
    private async void CopyAllClick(object sender, RoutedEventArgs e) => await CopyAsync(false);
    private async void CopyTranslationSelectionClick(object sender, RoutedEventArgs e) => await CopyAsync(true, true);
    private async void CopyTranslationAllClick(object sender, RoutedEventArgs e) => await CopyAsync(false, true);
    private async void TranslateAllClick(object sender, RoutedEventArgs e) => await TranslateAsync(false);
    private async void TranslateSelectionClick(object sender, RoutedEventArgs e) => await TranslateAsync(true);
    private void CancelTranslationClick(object sender, RoutedEventArgs e) => CancelTranslation("已取消翻译，原文和已有译文已保留。");
    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new TranslationSettingsWindow { Owner = this };
        dialog.ShowDialog();
        if (dialog.WasSaved) StatusText.Text = "翻译配置已保存，可点击翻译；清空密钥时仅保留离线 OCR。";
    }
    private void MergeClick(object sender, RoutedEventArgs e) => MergeLines();
    internal void MergeLines()
    {
        if (busy) return;
        string merged = Regex.Replace(ContentText.Text, @"[ \t]*\r?\n[ \t]*", " ");
        ContentText.BeginChange(); ContentText.SelectAll(); ContentText.SelectedText = merged; ContentText.EndChange();
        ContentText.CaretIndex = ContentText.Text.Length;
        StatusText.Text = "已合并换行，可按 Ctrl + Z 撤销。";
    }
    internal async Task TranslateAsync(bool selectedOnly)
    {
        if (busy || translation is not null || lifetime.IsCancellationRequested) return;
        string text = selectedOnly ? ContentText.SelectedText : ContentText.Text;
        string source = (string)((ComboBoxItem)SourceLanguage.SelectedItem).Tag;
        string target = (string)((ComboBoxItem)TargetLanguage.SelectedItem).Tag;
        if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = selectedOnly ? "请先在原文中选中需要翻译的文字。" : "请先输入需要翻译的原文。"; return; }
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long version = ++translationVersion;
        translation = pending;
        RefreshButtons();
        StatusText.Text = selectedOnly ? "正在翻译选中文字… Esc 可取消。" : "正在翻译全部原文… Esc 可取消。";
        try
        {
            var settings = loadTranslationSettings();
            var result = await translationService.TranslateAsync(text, source, target, settings, pending.Token);
            if (pending.IsCancellationRequested || version != translationVersion || lifetime.IsCancellationRequested) return;
            applyingTranslation = true;
            try { TranslationText.Text = result.Text; }
            finally { applyingTranslation = false; }
            staleTranslation = false;
            string scope = selectedOnly ? "选中部分" : "全部原文";
            TranslationStateText.Text = scope + " → " + (target == "en" ? "英文" : "简体中文") + " · 可编辑";
            TranslationStateText.ToolTip = selectedOnly ? "本次翻译的选中文字：\n" + text : "本次翻译全部原文。";
            StatusText.Text = "翻译完成。" + (result.DetectedLanguage is { } detected ? "检测到的原文语言：" + LanguageName(detected) + "。" : "") + "可编辑并复制译文。";
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (TranslationException ex) { if (version == translationVersion && !lifetime.IsCancellationRequested) StatusText.Text = ex.Message; }
        catch { if (version == translationVersion && !lifetime.IsCancellationRequested) StatusText.Text = "翻译失败，请检查设置后重试。原文和已有译文已保留。"; }
        finally
        {
            if (ReferenceEquals(translation, pending)) translation = null;
            RefreshButtons();
        }
    }
    private static string LanguageName(string language) => language switch { "en" => "英文", "zh-Hans" or "zh" => "简体中文", "zh-Hant" => "繁体中文", _ => "其他语言" };
    internal void CancelTranslation(string? message = null)
    {
        var pending = translation;
        translation = null;
        translationVersion++;
        pending?.Cancel();
        if (pending is not null && message is not null) StatusText.Text = message;
        RefreshButtons();
    }
    internal async Task CopyAsync(bool selectedOnly, bool translated = false)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        var box = translated ? TranslationText : ContentText;
        string text = selectedOnly ? box.SelectedText : box.Text;
        if (text.Length == 0) return;
        // Explicit copies use the visible edited text, even when it is marked stale.
        if (translated || !selectedOnly) CancelTranslation();
        busy = true; ContentText.IsReadOnly = true; TranslationText.IsReadOnly = true;
        RefreshButtons();
        try
        {
            await copyText(text, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (selectedOnly) StatusText.Text = translated ? "已复制选中译文，可继续编辑。" : "已复制选中文字，可继续编辑。";
            else { ActionCompleted = true; Close(); }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { StatusText.Text = "复制失败，请重试。编辑内容已保留。"; }
        finally { busy = false; ContentText.IsReadOnly = false; TranslationText.IsReadOnly = false; RefreshButtons(); }
    }
    private void RefreshButtons()
    {
        if (!initialized) return;
        CopyAllButton.IsEnabled = !busy && ContentText.Text.Length > 0;
        CopySelectionButton.IsEnabled = !busy && ContentText.SelectionLength > 0;
        CopyTranslationAllButton.IsEnabled = !busy && TranslationText.Text.Length > 0;
        CopyTranslationSelectionButton.IsEnabled = !busy && TranslationText.SelectionLength > 0;
        MergeButton.IsEnabled = !busy && ContentText.Text.Contains('\n');
        TranslateAllButton.IsEnabled = !busy && translation is null && !string.IsNullOrWhiteSpace(ContentText.Text);
        TranslateSelectionButton.IsEnabled = !busy && translation is null && ContentText.SelectionLength > 0;
        CancelTranslationButton.IsEnabled = translation is not null;
        SettingsButton.IsEnabled = !busy && translation is null;
    }
    internal string EditedText { get => ContentText.Text; set => ContentText.Text = value; }
    internal string EditedTranslation { get => TranslationText.Text; set => TranslationText.Text = value; }
    internal string Status => StatusText.Text;
    internal bool IsTranslationStale => staleTranslation;
    internal bool IsTranslating => translation is not null;
    internal void SelectText(int start, int length) => ContentText.Select(start, length);
    internal void SelectTranslation(int start, int length) => TranslationText.Select(start, length);
    internal void UndoEdit() => ContentText.Undo();
    internal void CopySelectionCommand() => ApplicationCommands.Copy.Execute(null, ContentText);
    internal void CopyTranslationCommand() => ApplicationCommands.Copy.Execute(null, TranslationText);
    internal void SetLanguages(int source, int target) { SourceLanguage.SelectedIndex = source; TargetLanguage.SelectedIndex = target; }
}
