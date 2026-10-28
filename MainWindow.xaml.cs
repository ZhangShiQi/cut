using System;
using System.ComponentModel;
using System.Windows;
using Cut.Services;

namespace Cut;

public partial class MainWindow : Window
{
    private readonly App app;
    private readonly IStartupRegistration startup;
    private bool refreshingStartup;
    private bool lastStartupEnabled;
    public MainWindow(App app, HotkeySettings settings) : this(app, settings, new StartupRegistration()) { }
    internal MainWindow(App app, HotkeySettings settings, IStartupRegistration startup)
    {
        InitializeComponent();
        this.app = app;
        this.startup = startup;
        UpdatePanel.DataContext = app.Updates;
        MaxHeight = SystemParameters.WorkArea.Height;
        for (char c = 'A'; c <= 'Z'; c++) KeyChoice.Items.Add(c.ToString());
        CtrlCheck.IsChecked = (settings.Modifiers & 2) != 0;
        AltCheck.IsChecked = (settings.Modifiers & 1) != 0;
        ShiftCheck.IsChecked = (settings.Modifiers & 4) != 0;
        KeyChoice.SelectedItem = ((char)settings.Key).ToString();
        RefreshStartup();
        Activated += (_, _) => RefreshStartup();
    }
    private void RefreshStartup()
    {
        refreshingStartup = true;
        try
        {
            lastStartupEnabled = startup.IsEnabled;
            StartupCheck.IsChecked = lastStartupEnabled;
            StartupStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
            StartupStatus.Text = !lastStartupEnabled ? "登录 Windows 后自动在托盘运行，不弹出设置窗口。"
                : startup.IsCurrentLocation ? "已开启：登录 Windows 后在托盘运行。移动程序后需重新开启。"
                : "已开启，但启动位置与当前程序不同。取消勾选后重新开启可更新位置。";
        }
        catch (Exception ex)
        {
            StartupStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            StartupStatus.Text = "无法读取自动启动设置：" + ex.Message;
        }
        finally { refreshingStartup = false; }
    }
    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        if (refreshingStartup || startup is null) return;
        try { startup.SetEnabled(StartupCheck.IsChecked == true); RefreshStartup(); }
        catch (Exception ex)
        {
            refreshingStartup = true; StartupCheck.IsChecked = lastStartupEnabled; refreshingStartup = false;
            StartupStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            StartupStatus.Text = "自动启动设置未保存：" + ex.Message;
        }
    }
    public void SetStatus(string text) => StatusText.Text = text;
    private void ApplyHotkey(object sender, RoutedEventArgs e)
    {
        uint modifiers = (CtrlCheck.IsChecked == true ? 2u : 0) | (AltCheck.IsChecked == true ? 1u : 0) | (ShiftCheck.IsChecked == true ? 4u : 0);
        if (modifiers == 0) { SetStatus("请至少选择一个修饰键。"); return; }
        if (KeyChoice.SelectedItem is string key) app.ChangeHotkey(new(modifiers, key[0]));
    }
    private async void StartCapture(object sender, RoutedEventArgs e) => await app.StartCaptureAsync();
    private async void CheckUpdates(object sender, RoutedEventArgs e) { if (app.Updates is not null) await app.Updates.CheckAsync(); }
    private void CancelUpdate(object sender, RoutedEventArgs e) => app.Updates?.Cancel();
    private void ChangeAutomaticUpdate(object sender, RoutedEventArgs e)
    {
        if (app.Updates is null) return;
        try { app.Updates.SetAutomatic(AutoUpdateCheck.IsChecked == true); }
        catch (Exception ex) { AutoUpdateCheck.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, app.Updates.Automatic); SetStatus("更新设置未保存：" + ex.Message); }
    }
    private void InstallUpdate(object sender, RoutedEventArgs e)
    {
        try { app.Updates?.Install(); } catch (Exception ex) { SetStatus(ex.Message); }
    }
    private void SaveReleaseToken(object sender, RoutedEventArgs e)
    {
        try { Updates.UpdateCredentials.Save(ReleaseToken.Password); ReleaseToken.Clear(); SetStatus("更新仓库读取令牌已保存。留空保存可清除令牌。"); }
        catch (Exception ex) { SetStatus("读取令牌未保存：" + ex.Message); }
    }
    private void OpenTranslationSettings(object sender, RoutedEventArgs e) => new TranslationSettingsWindow { Owner = this }.ShowDialog();
    private void OpenAiSettings(object sender, RoutedEventArgs e) => new AiSettingsWindow { Owner = this }.ShowDialog();
    private void OpenVisionSettings(object sender, RoutedEventArgs e) => new AiSettingsWindow(true) { Owner = this }.ShowDialog();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!app.IsExiting) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
