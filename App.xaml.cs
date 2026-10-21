using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cut.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Cut;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private HotkeyService? hotkey;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private MainWindow? settingsWindow;
    private CaptureSession? session;
    private HotkeySettings settings = new();
    private bool starting;
    private bool hotkeyRegistered;
    public bool IsExiting { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTests.Run()); return; }
        if (e.Args.Contains("--qr-test")) { Shutdown(await QrTests.RunAsync()); return; }
        if (e.Args.Contains("--smoke-test")) { Shutdown(await SelfTests.SmokeAsync()); return; }
        instanceMutex = new Mutex(true, "Local\\Cut.Screenshot.SingleInstance", out bool created);
        if (!created) { MessageBox.Show("Cut 已在运行，请通过托盘图标或截图快捷键使用。", "Cut"); Shutdown(); return; }
        settings = HotkeySettings.Load();
        hotkey = new HotkeyService();
        hotkey.Pressed += async () => await StartCaptureAsync();
        settingsWindow = new MainWindow(this, settings);
        MainWindow = settingsWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("开始截图", null, async (_, _) => await StartCaptureAsync());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        trayIcon = LoadTrayIcon();
        tray = new Forms.NotifyIcon { Text = "Cut · 截图", Icon = trayIcon, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowSettings();
        ChangeHotkey(settings, persist: false);
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        settingsWindow.Show();
    }
    public void ChangeHotkey(HotkeySettings requested, bool persist = true)
    {
        if (hotkeyRegistered && requested == settings) { settingsWindow?.SetStatus("当前快捷键：" + settings); return; }
        if (hotkey?.TryRegister(requested.Modifiers, requested.Key) != true)
        {
            string reason = hotkey?.LastError == 1409 ? "快捷键已被占用，请换一个组合。" : $"快捷键注册失败（错误 {hotkey?.LastError}）。";
            settingsWindow?.SetStatus(reason + (hotkeyRegistered ? "当前仍为：" + settings : "仍可点击“开始截图”。"));
            return;
        }
        hotkeyRegistered = true;
        settings = requested;
        settingsWindow?.SetStatus("当前快捷键：" + settings);
        if (tray is not null) tray.Text = "Cut · " + settings;
        if (persist)
        {
            try { settings.Save(); }
            catch (Exception ex) { settingsWindow?.SetStatus("快捷键已生效，但配置保存失败：" + ex.Message); }
        }
    }
    public async Task StartCaptureAsync()
    {
        if (starting || session is not null || IsExiting) return;
        starting = true;
        try
        {
            settingsWindow?.Hide();
            tray?.ContextMenuStrip?.Close();
            // Let DWM remove our settings/menu before freezing the desktop.
            await Task.Delay(160);
            if (IsExiting) return;
            session = new CaptureSession(CaptureService.Capture());
            session.Completed += () => session = null;
            session.Show();
        }
        catch (Exception ex)
        {
            session?.Abort();
            session = null;
            ShowSettings();
            MessageBox.Show(settingsWindow!, "无法开始截图：\n" + ex.Message, "Cut", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { starting = false; }
    }
    private void ShowSettings()
    {
        if (session is not null || IsExiting) return;
        settingsWindow?.Show();
        if (settingsWindow is not null) { settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate(); }
    }
    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/Cut.Tray.ico"));
        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        session?.Abort();
        if (tray is null || IsExiting) return;
        var previous = trayIcon;
        trayIcon = LoadTrayIcon();
        tray.Icon = trayIcon;
        previous?.Dispose();
    }));
    private void ExitApplication() { IsExiting = true; session?.Abort(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        hotkey?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
