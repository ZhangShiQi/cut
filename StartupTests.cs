using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using Cut.Services;
using Microsoft.Win32;

namespace Cut;

internal static class StartupTests
{
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private sealed class DeniedRegistration : IStartupRegistration
    {
        public bool IsEnabled => false;
        public bool IsCurrentLocation => false;
        public void SetEnabled(bool enabled) => throw new UnauthorizedAccessException("Synthetic access denied");
    }
    internal static Task<int> RunAsync(App app)
    {
        string root = @"Software\Cut\Tests\Startup-" + Guid.NewGuid().ToString("N");
        var log = new StringBuilder("Startup tests: isolated HKCU test key only; actual Windows Run entry is read-only.\n");
        MainWindow? window = null;
        Directory.CreateDirectory("artifacts");
        RegistryKey? realRun = null;
        try
        {
            realRun = Registry.CurrentUser.OpenSubKey(StartupRegistration.RunKey, false);
            object? before = realRun?.GetValue(StartupRegistration.ValueName);
            string directory = Path.GetFullPath("artifacts/启动测试 with spaces"); Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "Cut.exe"); File.Copy(Environment.ProcessPath!, executable, true);
            var service = new StartupRegistration(Registry.CurrentUser, root, () => executable);
            Check(!service.IsEnabled, "Absent setting defaults off");
            using (var absent = Registry.CurrentUser.OpenSubKey(root)) Check(absent is null, "Reading does not create a key");
            service.SetEnabled(true);
            using (var key = Registry.CurrentUser.OpenSubKey(root, true)!)
            {
                Check(key.GetValue(StartupRegistration.ValueName) as string == "\"" + executable + "\" --autostart", "Quoted absolute path, Unicode, spaces and quiet-start argument");
                Check(key.GetValueKind(StartupRegistration.ValueName) == RegistryValueKind.String, "String registry type");
                key.SetValue("UnrelatedApp", "preserve me");
            }
            Check(new StartupRegistration(Registry.CurrentUser, root, () => executable).IsEnabled && service.IsCurrentLocation, "Persisted registration reload");
            string movedDirectory = Path.Combine(directory, "moved"); Directory.CreateDirectory(movedDirectory);
            string movedExe = Path.Combine(movedDirectory, "Cut.exe"); File.Copy(executable, movedExe, true);
            var moved = new StartupRegistration(Registry.CurrentUser, root, () => movedExe);
            Check(moved.IsEnabled && !moved.IsCurrentLocation, "Moved portable folder detected");
            moved.SetEnabled(true); Check(moved.IsCurrentLocation, "Enable updates executable path");
            service.SetEnabled(false); service.SetEnabled(false);
            using (var key = Registry.CurrentUser.OpenSubKey(root)!) Check((string?)key.GetValue("UnrelatedApp") == "preserve me", "Disable touches only Cut's value");
            Check(!service.IsEnabled, "Disable persists and is idempotent");
            foreach (string invalid in new[] { "Cut.exe", Path.Combine(directory, "missing", "Cut.exe"), executable.Replace("Cut.exe", "dotnet.exe"), "C:\\bad\"path\\Cut.exe" })
            {
                bool rejected = false;
                try { StartupRegistration.Command(invalid); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Invalid executable rejected");
            }
            log.AppendLine("PASS default-off/read-only load, real isolated registry enable/reload/disable, quoted path, spaces/Unicode, moved folder, invalid paths and unrelated entry preservation");
            window = new MainWindow(app, new(), service); window.Show();
            var toggle = (CheckBox)window.FindName("StartupCheck");
            Check(toggle.IsChecked == false, "Settings reflect disabled registration");
            toggle.IsChecked = true; Check(service.IsEnabled && service.IsCurrentLocation, "Checkbox persists immediately");
            ScrollTests.Preview(window, "artifacts/startup-settings-preview.png");
            toggle.IsChecked = false; Check(!service.IsEnabled, "Checkbox removes registration"); window.Hide();
            window = new MainWindow(app, new(), new DeniedRegistration()); window.Show();
            toggle = (CheckBox)window.FindName("StartupCheck"); toggle.IsChecked = true;
            Check(toggle.IsChecked == false && ((TextBlock)window.FindName("StartupStatus")).Text.Contains("未保存"), "Write failure restores checkbox and reports error");
            window.Hide();
            Check(App.IsAutoStart(new[] { "--autostart" }) && App.IsAutoStart(new[] { "--AUTOSTART" }), "Automatic startup flag");
            Check(!App.IsAutoStart(Array.Empty<string>()) && !App.IsAutoStart(new[] { "--autostart-other" }), "Manual startup and partial argument retain normal UI");
            Check(Equals(before, realRun?.GetValue(StartupRegistration.ValueName)), "Real login entry remains untouched");
            log.AppendLine("PASS settings checkbox, readback, failed-write rollback, startup launch policy and no real login-entry changes");
            log.AppendLine("ALL STARTUP CHECKS PASSED");
            File.WriteAllText("artifacts/startup-test.txt", log.ToString()); return Task.FromResult(0);
        }
        catch (Exception ex) { window?.Hide(); File.WriteAllText("artifacts/startup-test.txt", log + ex.ToString()); return Task.FromResult(1); }
        finally
        {
            realRun?.Dispose();
            try { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }
            catch (Exception ex) { File.AppendAllText("artifacts/startup-test.txt", "Test-key cleanup: " + ex.Message + "\n"); }
        }
    }
}
