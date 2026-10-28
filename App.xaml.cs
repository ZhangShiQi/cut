using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cut.Services;
using Cut.UpdateEngine;
using Cut.Updates;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Cut;

public partial class App : Application
{
    private SingleInstanceHost? instanceHost;
    private HotkeyService? hotkey;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private MainWindow? settingsWindow;
    private CaptureSession? session;
    private HotkeySettings settings = new();
    private bool starting;
    private bool hotkeyRegistered;
    private string? notifiedPackage;
    public bool IsExiting { get; private set; }
    public UpdateCoordinator? Updates { get; internal set; }
    internal static bool IsSafeToRestart(bool starting, bool hasCapture, bool exiting, bool hasOtherWindow) => !starting && !hasCapture && !exiting && !hasOtherWindow;
    internal bool CanRestartForUpdate => IsSafeToRestart(starting, session is not null, IsExiting,
        Windows.OfType<Window>().Any(w => w != settingsWindow && w.IsVisible));

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--startup-test")) { Shutdown(await StartupTests.RunAsync(this)); return; }
        if (e.Args.Contains("--update-ui-test")) { Shutdown(await UpdateUiTests.RunAsync(this)); return; }
        if (e.Args.Contains("--self-test")) { Shutdown(SelfTests.Run()); return; }
        if (e.Args.Contains("--qr-test")) { Shutdown(await QrTests.RunAsync()); return; }
        if (e.Args.Contains("--window-test")) { Shutdown(await WindowSelectionTests.RunAsync()); return; }
        if (e.Args.Contains("--ocr-test")) { Shutdown(await OcrTests.RunAsync()); return; }
        if (e.Args.Contains("--translation-test")) { Shutdown(await TranslationTests.RunAsync()); return; }
        if (e.Args.Contains("--ai-test")) { Shutdown(await AiTests.RunAsync()); return; }
        if (e.Args.Contains("--vision-test")) { Shutdown(await VisionTests.RunAsync()); return; }
        if (e.Args.Contains("--annotation-test")) { Shutdown(await AnnotationTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-test")) { Shutdown(await ScrollTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-native-test")) { Shutdown(await ScrollTests.NativeAsync()); return; }
        if (e.Args.Contains("--auto-scroll-test")) { Shutdown(await AutoScrollTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-web-test")) { Shutdown(await ScrollWebTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-optimization-test")) { Shutdown(await ScrollOptimizationTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-permissive-test")) { Shutdown(await ScrollPermissiveTests.RunAsync()); return; }
        if (e.Args.Contains("--scroll-benchmark")) { Shutdown(await ScrollOptimizationTests.BenchmarkAsync()); return; }
        if (e.Args.Contains("--auto-scroll-native-test")) { Shutdown(await AutoScrollTests.NativeAsync()); return; }
        if (e.Args.Contains("--smoke-test")) { Shutdown(await SelfTests.SmokeAsync()); return; }
        try { await InitializeAsync(e.Args); }
        catch (Exception ex)
        {
            try { DurableFiles.Write(System.IO.Path.Combine(UpdatePaths.Data, "device", "startup-error.json"), new { error = ex.ToString() }); } catch { }
            if (!e.Args.Contains("--startup-pipe") && UpdatePaths.TestKey is null) MessageBox.Show("Cut 启动失败：\n" + ex.Message, "Cut");
            Shutdown(1);
        }
    }
    private async Task InitializeAsync(string[] args)
    {
        UpdatePaths.Configure(args);
        if (LauncherContext.ConfigureOrRedirect(args)) { Shutdown(); return; }
        bool autoStart = IsAutoStart(args);
        bool startupSmoke = args.Contains("--startup-smoke-test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        StartupHandshake? handshake = null;
        var pipeName = UpdatePaths.Argument(args, "--startup-pipe");
        if (LauncherContext.Root is not null && pipeName is null) throw new InvalidOperationException("请通过根目录 Cut.exe 启动。");
        if (pipeName is not null)
            handshake = await StartupHandshake.ConnectAsync(pipeName, UpdatePaths.Argument(args, "--startup-nonce") ?? "", timeout.Token);
        await using var startupHandshake = handshake;
        instanceHost = new SingleInstanceHost(startupSmoke ? "Cut.Tests.StartupSmokeTest" : UpdatePaths.TestKey);
        if (!instanceHost.IsPrimary)
        {
            if (!autoStart) await instanceHost.NotifyExistingAsync();
            Shutdown(); return;
        }
        settings = HotkeySettings.Load();
        hotkey = new HotkeyService();
        hotkey.Pressed += async () => await StartCaptureAsync();
        Updates = new UpdateCoordinator(() => CanRestartForUpdate, ExitApplication);
        settingsWindow = new MainWindow(this, settings);
        MainWindow = settingsWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("开始截图", null, async (_, _) => await StartCaptureAsync());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        trayIcon = LoadTrayIcon();
        tray = new Forms.NotifyIcon { Text = "Cut · 截图", Icon = trayIcon, Visible = handshake is null, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowSettings();
        Updates.PropertyChanged += (_, _) => NotifyUpdateReady();
        if (handshake is not null)
        {
            new System.Windows.Interop.WindowInteropHelper(settingsWindow).EnsureHandle();
            settingsWindow.Measure(new Size(settingsWindow.Width, settingsWindow.Height));
            settingsWindow.Arrange(new Rect(0, 0, settingsWindow.Width, settingsWindow.Height));
            settingsWindow.UpdateLayout();
            await Services.StartupHealth.CheckAsync(timeout.Token);
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await handshake.ReadyAsync(timeout.Token);
        }
        tray.Visible = true;
        if (LauncherContext.Root is not null && UpdatePaths.TestKey is null)
        {
            try { new StartupRegistration().MigrateLegacyEntry(); }
            catch (Exception ex) { settingsWindow.SetStatus("自动启动位置未更新：" + ex.Message); }
        }
        ChangeHotkey(settings, persist: false);
        instanceHost.StartListening(() => Dispatcher.BeginInvoke(new Action(ShowSettings)));
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        if (UpdatePaths.TestKey is null) Updates.Start();
        if (!autoStart) settingsWindow.Show();
        if (startupSmoke)
        {
            await Task.Delay(300);
            bool passed = autoStart && tray.Visible && !settingsWindow.IsVisible;
            System.IO.Directory.CreateDirectory("artifacts");
            System.IO.File.WriteAllText("artifacts/startup-smoke-test.txt", passed
                ? "PASS actual --autostart initialization: tray visible, settings hidden, startup entry not modified; no reboot performed.\n"
                : "FAIL actual automatic startup initialization\n");
            IsExiting = true; Shutdown(passed ? 0 : 1);
        }
    }
    internal static bool IsAutoStart(string[] args) => args.Any(arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));
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
        var aiSettings = Windows.OfType<AiSettingsWindow>().FirstOrDefault(w => w.IsVisible);
        if (aiSettings is not null) { aiSettings.Activate(); return; }
        var translationSettings = Windows.OfType<TranslationSettingsWindow>().FirstOrDefault(w => w.IsVisible);
        if (translationSettings is not null) { translationSettings.Activate(); return; }
        if (starting || session is not null || IsExiting) return;
        starting = true;
        try
        {
            settingsWindow?.Hide();
            tray?.ContextMenuStrip?.Close();
            // Let DWM remove our settings/menu before freezing the desktop.
            await Task.Delay(160);
            if (IsExiting) return;
            var windowTargets = WindowDetectionService.Capture();
            session = new CaptureSession(CaptureService.Capture(), windowTargets);
            session.Completed += () => { session = null; NotifyUpdateReady(); };
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
    private void NotifyUpdateReady()
    {
        if (Updates?.CanInstall != true || tray is null || !tray.Visible || !CanRestartForUpdate ||
            Updates.Available?.Sha256 == notifiedPackage || UpdatePaths.TestKey is not null) return;
        notifiedPackage = Updates.Available!.Sha256;
        tray.ShowBalloonTip(6000, "Cut 新版本已就绪", "完成当前编辑后，可在设置中点击“重启并更新”。", Forms.ToolTipIcon.Info);
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
        Updates?.Dispose();
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        hotkey?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        trayIcon?.Dispose();
        instanceHost?.Dispose();
        base.OnExit(e);
    }
}
