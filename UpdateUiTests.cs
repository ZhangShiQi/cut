using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Cut.UpdateEngine;
using Cut.Updates;

namespace Cut;

internal static class UpdateUiTests
{
    private sealed class MockHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK));
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task<int> RunAsync(App app)
    {
        var folder = Path.GetFullPath("artifacts/update-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var key = "Cut.Tests." + Guid.NewGuid().ToString("N"); File.WriteAllText(Path.Combine(folder, ".cut-update-test"), key);
        MainWindow? window = null;
        try
        {
            UpdatePaths.Configure(new[] { "--data-dir", folder, "--test-instance-key", key });
            var token = "synthetic-read-only-token";
            UpdateCredentials.Save(token);
            Check(await UpdateCredentials.ReadAsync(false, CancellationToken.None) == token, "DPAPI credential roundtrip");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "device", "release-token.bin"))).Contains(token), "Token stored as plaintext");
            UpdateCredentials.Save(""); Check(await UpdateCredentials.ReadAsync(false, CancellationToken.None) is null, "Clear optional credential");
            app.Updates = new UpdateCoordinator(() => false, () => throw new InvalidOperationException("Must not exit test"), new UpdateClient(new MockHttp(), (_, _) => Task.FromResult<string?>(null)));
            window = new MainWindow(app, new()); window.Show();
            await app.Updates.CheckAsync();
            Check(app.Updates.Status.Contains("没有更新"), "No-release UI state");
            app.Updates.SetAutomatic(false);
            using (var reload = new UpdateCoordinator(() => true, () => { }, new UpdateClient(new MockHttp()))) Check(!reload.Automatic, "Automatic preference persistence");
            Check(!app.Updates.CanInstall && app.Updates.CanCheck, "Unavailable update button state");
            Check(!App.IsSafeToRestart(true, false, false, false) && !App.IsSafeToRestart(false, true, false, false) &&
                !App.IsSafeToRestart(false, false, true, false) && !App.IsSafeToRestart(false, false, false, true) && App.IsSafeToRestart(false, false, false, false), "Capture/edit/exit restart guard");
            ((FrameworkElement)window.FindName("UpdatePanel")).BringIntoView();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            ScrollTests.Preview(window, "artifacts/update-settings-preview.png");
            File.WriteAllText("artifacts/update-ui-test.txt", "PASS settings UI, release state, encrypted token save/reload/clear, preference persistence and active-session restart guard\nALL UPDATE UI CHECKS PASSED\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText("artifacts/update-ui-test.txt", ex.ToString()); return 1; }
        finally { window?.Hide(); app.Updates?.Dispose(); app.Updates = null; UpdatePaths.Configure(Array.Empty<string>()); }
    }
}
