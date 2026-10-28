using System.Diagnostics;
using System.Text.Json;
using Cut.UpdateEngine;

internal static partial class Program
{
    private static int PortableSmoke(string package, string fixture)
    {
        fixture = Path.GetFullPath(fixture); package = Path.GetFullPath(package);
        if (Directory.Exists(fixture)) throw new IOException("Portable smoke directory already exists.");
        Directory.CreateDirectory(fixture);
        var root = Path.Combine(fixture, "便携 程序's");
        using var zip = System.IO.Compression.ZipFile.OpenRead(package);
        using var reader = new StreamReader(zip.Entries.Single(e => e.FullName.EndsWith("/release-manifest.json", StringComparison.Ordinal)).Open());
        using var manifest = JsonDocument.Parse(reader.ReadToEnd());
        var version = manifest.RootElement.GetProperty("version").GetString()!;
        UpdatePackage.Extract(package, root, version);
        var data = Path.Combine(fixture, "隔离 配置's"); Directory.CreateDirectory(data);
        var key = "Cut.Tests." + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(data, ".cut-update-test"), key);
        File.WriteAllText(Path.Combine(data, "settings.json"), "{\"Modifiers\":3,\"Key\":65}");
        // These bytes stand in for opaque DPAPI configuration. They must never be rewritten by updating.
        foreach (var name in new[] { "ai.json", "vision.json", "translation.json" }) File.WriteAllText(Path.Combine(data, name), "{}");
        var hashes = Directory.EnumerateFiles(data).ToDictionary(p => p, DurableFiles.Hash);
        var launcherHash = DurableFiles.Hash(Path.Combine(root, "Cut.exe"));
        var store = new LauncherStore(root);
        void AssertData() { foreach (var pair in hashes) Check(DurableFiles.Hash(pair.Key) == pair.Value, "Portable update changed configuration: " + Path.GetFileName(pair.Key)); }
        void StopApps()
        {
            foreach (var process in Process.GetProcessesByName("Cut"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is string image && UpdatePackage.IsWithin(image, root))
                        { process.Kill(true); Check(process.WaitForExit(10000), "Test runtime would not exit"); }
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }
        void CheckRuntime()
        {
            var expected = DurableFiles.Child(VersionLayout.Slot(root, store.ReadState().Active), VersionLayout.Runtime);
            var found = false;
            foreach (var process in Process.GetProcessesByName("Cut"))
                using (process)
                    try { if (process.MainModule?.FileName == expected) { found = true; Check(!process.HasExited, "Real WPF startup exited"); } } catch (InvalidOperationException) { }
            Check(found, "No real WPF runtime after startup confirmation");
        }
        try
        {
            var info = new ProcessStartInfo(Path.Combine(root, "Cut.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture };
            foreach (var arg in new[] { "--data-dir", data, "--test-instance-key", key, "--autostart" }) info.ArgumentList.Add(arg);
            using (var launcher = Process.Start(info)!)
            {
                if (!launcher.WaitForExit(90000)) { launcher.Kill(true); throw new TimeoutException("Real launcher startup timed out."); }
                var errorPath = Path.Combine(store.DirectoryPath, "launcher-error.json");
                Check(launcher.ExitCode == 0, "Real launcher failed: " + (File.Exists(errorPath) ? File.ReadAllText(errorPath) : "no diagnostic"));
            }
            CheckRuntime(); AssertData(); StopApps();
            Check(store.ReadState().Initialized, "Bootstrap not confirmed");
            Console.WriteLine("PASS real self-contained stable launcher, WPF/tray initialization and synthetic OCR health check");
            var request = new UpdateRequest(root, data, package, DurableFiles.Hash(package), version, int.MaxValue, 0, true);
            DurableFiles.Write(store.QueueFile, request);
            new LauncherEngine(root, new LauncherOptions { InstanceKey = key, AllowReinstallForTests = true }).Run(data, true);
            var active = store.ReadState().Active;
            Check(active != "bootstrap" && store.ReadState().Previous == "bootstrap", "Real candidate did not commit");
            CheckRuntime(); AssertData(); StopApps();
            Check(DurableFiles.Hash(Path.Combine(root, "Cut.exe")) == launcherHash, "Stable launcher replaced");
            Console.WriteLine("PASS real ZIP staging, complete component verification, configuration snapshot and candidate commit");
            DurableFiles.Write(store.QueueFile, request);
            var attempts = 0;
            new LauncherEngine(root, new LauncherOptions
            {
                InstanceKey = key, AllowReinstallForTests = true,
                RuntimeCommand = runtime =>
                {
                    var start = new ProcessStartInfo(runtime);
                    if (attempts++ == 0) { start.ArgumentList.Add("--startup-nonce"); start.ArgumentList.Add("wrong-identity"); }
                    return start;
                }
            }).Run(data, true);
            Check(store.ReadState().Active == active && LauncherStore.IsRejected(root, request.Sha256), "Failed WPF trial did not roll back/quarantine");
            CheckRuntime(); AssertData(); StopApps();
            Console.WriteLine("PASS actual WPF startup rejection, snapshot recovery, previous runtime restart and package quarantine");
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"Modifiers\":3,\"Key\":66}");
            var userHash = DurableFiles.Hash(Path.Combine(data, "settings.json"));
            File.AppendAllText(DurableFiles.Child(VersionLayout.Slot(root, active), VersionLayout.Runtime), "corruption");
            new LauncherEngine(root, new LauncherOptions { InstanceKey = key }).Run(data, true);
            Check(store.ReadState().Active == "bootstrap", "Broken confirmed runtime did not select previous version");
            Check(DurableFiles.Hash(Path.Combine(data, "settings.json")) == userHash, "Late rollback overwrote new user changes");
            CheckRuntime(); StopApps();
            Console.WriteLine("PASS confirmed runtime corruption falls back while preserving newer configuration");
            DurableFiles.Write(Path.Combine(fixture, "result.json"), new { success = true, version, packageSha256 = request.Sha256, checks = 4 });
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(fixture, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
        finally { StopApps(); }
    }
}
