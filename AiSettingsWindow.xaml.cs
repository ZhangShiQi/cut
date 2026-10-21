using System;
using System.Threading;
using System.Threading.Tasks;
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
    public bool WasSaved { get; private set; }
    public AiSettingsWindow() : this(new(), new CompatibleAiService()) { }
    internal AiSettingsWindow(AiSettingsStore store, IAiService service)
    {
        InitializeComponent(); this.store = store; this.service = service;
        try { SetFields(store.Load()); }
        catch (AiException ex) { SetFields(new()); StatusText.Text = ex.Message; }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Closed += (_, _) => lifetime.Cancel();
    }
    internal void SetFields(AiSettings value)
    { EndpointText.Text = value.Endpoint; ModelText.Text = value.Model; KeyText.Password = value.ApiKey; }
    private AiSettings ReadFields() => new() { Endpoint = EndpointText.Text.Trim(), Model = ModelText.Text.Trim(), ApiKey = KeyText.Password.Trim() };
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
            var config = ReadFields(); config.Validate();
            await service.GenerateAsync(new("Hello", AiTask.Custom, "请只回复 OK。"), config, _ => Task.CompletedTask, lifetime.Token);
            if (!lifetime.IsCancellationRequested) StatusText.Text = "连接成功，已收到模型文字回答。点击保存后生效。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (AiException ex) { if (!lifetime.IsCancellationRequested) StatusText.Text = ex.Message; }
        catch { if (!lifetime.IsCancellationRequested) StatusText.Text = "连接测试失败，请检查配置与接口兼容性。"; }
        finally { testing = false; SetBusy(); }
    }
    private void SetBusy() { SettingsFields.IsEnabled = !testing; SaveButton.IsEnabled = !testing; TestButton.IsEnabled = !testing; }
    internal string Status => StatusText.Text;
}
