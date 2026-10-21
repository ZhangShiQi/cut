using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Cut.Services;

namespace Cut;

public partial class AiSettingsWindow : Window
{
    private readonly AiSettingsStore store;
    private readonly IAiService service;
    private readonly CancellationTokenSource lifetime = new();
    private bool testing;
    private readonly bool vision;
    private readonly Func<AiSettings> loadTextSettings;
    public bool WasSaved { get; private set; }
    public AiSettingsWindow() : this(false) { }
    public AiSettingsWindow(bool vision) : this(vision ? AiSettingsStore.Vision() : new(), new CompatibleAiService(), vision) { }
    internal AiSettingsWindow(AiSettingsStore store, IAiService service, bool vision = false, Func<AiSettings>? loadTextSettings = null)
    {
        InitializeComponent(); this.store = store; this.service = service; this.vision = vision;
        this.loadTextSettings = loadTextSettings ?? new AiSettingsStore().Load;
        if (vision)
        {
            Title = "Cut · 视觉模型设置"; HeaderText.Text = "视觉模型设置"; Height = 720;
            IntroText.Text = "选择支持图片输入的 Chat Completions 模型。视觉配置单独保存，不影响文字 AI。";
            ModelHint.Text = "填写支持图片输入的模型 ID；仅文字连接成功不代表可以看图。";
            InheritCheck.Visibility = Visibility.Visible; TestButton.Content = "测试图片理解";
            PrivacyText.Text = "点击分析时会发送选区图片与问题至配置的服务，可能按用量收费。密钥加密保存。测试仅发送程序生成的验证码图片，不使用你的截图；成功后仍需保存。";
            StatusText.Text = "保存后可通过截图工具栏或 Ctrl + I 使用 AI 看图。";
        }
        try { SetFields(store.Load()); }
        catch (AiException ex) { SetFields(new()); StatusText.Text = ex.Message; }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => lifetime.Cancel();
    }
    internal void SetFields(AiSettings value)
    { EndpointText.Text = value.Endpoint; ModelText.Text = value.Model; KeyText.Password = value.ApiKey; InheritCheck.IsChecked = vision && value.UseTextService; UpdateInheritance(); }
    private AiSettings ReadFields() => new() { Endpoint = EndpointText.Text.Trim(), Model = ModelText.Text.Trim(), ApiKey = KeyText.Password.Trim(), UseTextService = vision && InheritCheck.IsChecked == true };
    private void InheritanceChanged(object sender, RoutedEventArgs e) => UpdateInheritance();
    private void UpdateInheritance()
    {
        if (EndpointText is null || KeyText is null) return;
        EndpointText.IsEnabled = KeyText.IsEnabled = InheritCheck.IsChecked != true;
        if (InheritedInfo is null) return;
        InheritedInfo.Visibility = InheritCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (InheritCheck.IsChecked == true)
        {
            try { InheritedInfo.Text = "实际使用文字 AI 地址：" + loadTextSettings().Endpoint; }
            catch { InheritedInfo.Text = "无法读取文字 AI 配置，请先完成文字 AI 服务设置。"; }
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void SaveClick(object sender, RoutedEventArgs e) => SaveSettings();
    internal void SaveSettings()
    {
        if (testing || lifetime.IsCancellationRequested) return;
        try { store.Save(ReadFields()); WasSaved = true; Close(); }
        catch (AiException ex) { StatusText.Text = ex.Message; }
        catch { StatusText.Text = "保存失败，请检查当前用户配置目录的写入权限后重试。"; }
    }
    private async void TestClick(object sender, RoutedEventArgs e) => await TestConnectionAsync();
    internal async Task TestConnectionAsync()
    {
        if (testing || lifetime.IsCancellationRequested) return;
        testing = true; SetBusy(); StatusText.Text = "正在测试连接… 关闭窗口可取消。";
        try
        {
            var config = ReadFields();
            if (vision)
            {
                config = config.ResolveVision(loadTextSettings);
                var probe = VisionImage.CreateProbe(); ProbeImage.Source = probe.Image; ProbeImage.Visibility = Visibility.Visible;
                var image = await VisionImage.PrepareAsync(probe.Image, lifetime.Token);
                var answer = new StringBuilder();
                await service.GenerateAsync(new("", AiTask.Custom, "请只读出图片中的六位数字验证码，不要添加其他内容。", image), config,
                    part => { answer.Append(part); return Task.CompletedTask; }, lifetime.Token);
                if (!lifetime.IsCancellationRequested) StatusText.Text = Regex.IsMatch(answer.ToString(), @"(?<!\d)" + probe.Code + @"(?!\d)")
                    ? "图片测试通过：正确读出图中验证码。点击保存后生效；复杂截图效果仍需实际使用核对。"
                    : "收到服务回答，但未正确读出验证码，图片理解未通过。请核对视觉模型和接口支持。";
            }
            else
            {
                config.Validate();
                await service.GenerateAsync(new("Hello", AiTask.Custom, "请只回复 OK。"), config, _ => Task.CompletedTask, lifetime.Token);
                if (!lifetime.IsCancellationRequested) StatusText.Text = "连接成功，已收到模型文字回答。点击保存后生效。";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AiException ex) { if (!lifetime.IsCancellationRequested) StatusText.Text = ex.Message; }
        catch { if (!lifetime.IsCancellationRequested) StatusText.Text = "连接测试失败，请检查配置与接口兼容性。"; }
        finally { testing = false; SetBusy(); }
    }
    private void SetBusy() { SettingsFields.IsEnabled = !testing; SaveButton.IsEnabled = !testing; TestButton.IsEnabled = !testing; }
    internal string Status => StatusText.Text;
}
