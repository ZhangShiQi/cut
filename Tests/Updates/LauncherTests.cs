using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Cut.UpdateEngine;

internal static partial class Program
{
    private static ProcessStartInfo TestChild(params string[] args)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    private static int LauncherWorker(string[] args)
    {
        var root = args[1]; var data = args[2]; var checkpoint = args[3]; var signal = args[4];
        try
        {
            var options = new LauncherOptions
            {
                InstanceKey = File.ReadAllText(Path.Combine(root, "test-key.txt")),
                StartupTimeout = TimeSpan.FromSeconds(12), StabilityTime = TimeSpan.FromMilliseconds(150),
                RuntimeCommand = runtime => TestChild("--health-probe", File.ReadAllText(runtime)),

                Checkpoint = phase =>
                {
                    if (phase != checkpoint) return;
                    File.WriteAllText(signal, phase);
                    Thread.Sleep(Timeout.Infinite);
                }
            };
            return new LauncherEngine(root, options).Run(data);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "worker-error.txt"), ex.ToString()); return 2; }
    }
    private static int HealthProbe(string[] args)
    {
        var behavior = args[1];
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        var root = Arg("--launcher-root"); var data = Arg("--data-dir");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var session = StartupHandshake.ConnectAsync(Arg("--startup-pipe"), Arg("--startup-nonce"), timeout.Token).GetAwaiter().GetResult();
            try
            {
            using var instance = new SingleInstanceHost(Arg("--test-instance-key"));
            if (!instance.IsPrimary) return 7;
            instance.StartListening(() => { });
            using var process = Process.GetCurrentProcess();
            DurableFiles.Write(Path.Combine(root, "test-children", process.Id + ".json"), new { pid = process.Id, started = process.StartTime.ToUniversalTime().Ticks });
            if (behavior is "mutate" or "crash" or "hang" or "ready-crash")
            {
                File.WriteAllText(Path.Combine(data, "notes", "content.txt"), "candidate initialization");
                File.WriteAllText(Path.Combine(data, "trial-only.txt"), "uncommitted file");
            }
            if (behavior == "crash") return 9;
            if (behavior == "hang") { timeout.Token.WaitHandle.WaitOne(); return 10; }
            if (behavior == "ready-crash")
            {
                // Send readiness, then terminate while the launcher's stabilization interval is still active.
                _ = session.ReadyAsync(timeout.Token);
                Thread.Sleep(20); return 11;
            }
            session.ReadyAsync(timeout.Token).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "test-active.json"), JsonSerializer.Serialize(new { pid = process.Id, started = process.StartTime.ToUniversalTime().Ticks }));
            if (behavior == "after-commit-crash") return 12;
            while (!File.Exists(Path.Combine(root, "test-stop"))) { timeout.Token.ThrowIfCancellationRequested(); Thread.Sleep(25); }
            return 0;
            }
            finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(root, "probe-error-" + Environment.ProcessId + ".txt"), ex.ToString()); return 8; }
    }
    private static void ModernPayload(string root, string version, string behavior)
    {
        void Write(string relative, string value)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value, new UTF8Encoding(false));
        }
        Write("Cut.exe", "stable launcher bytes"); Write(VersionLayout.Runtime, behavior);
        Write("portable.flag", "portable"); foreach (var name in new[] { "Models/manifest.json", "Models/ch_PP-OCRv5_det_mobile.onnx", "Models/ch_PP-OCRv5_rec_mobile.onnx", "onnxruntime.dll", "OpenCvSharpExtern.dll" }) Write("tools/runtime/" + name, "fixture");
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(root, p).Replace('\\', '/'), DurableFiles.Hash);
        DurableFiles.Write(Path.Combine(root, "release-manifest.json"), new
        {
            application = "Cut", version, runtimeIdentifier = "win-x64", selfContained = true, singleFile = false,
            executableSha256 = files["Cut.exe"], formatVersion = 2, launcherProtocol = VersionLayout.Protocol,
            dataSchemaVersion = VersionLayout.DataSchema, runtimePath = VersionLayout.Runtime, runtimeSha256 = files[VersionLayout.Runtime], files
        });
    }
    private static (string Root, string Data, LauncherStore Store) LauncherFixture(string name, string behavior = "good", bool externalData = false)
    {
        var folder = Path.Combine(_root, name); Directory.CreateDirectory(folder);
        var root = Path.Combine(folder, "portable 中文's"); ModernPayload(root, "0.1.0", "good");
        var data = externalData ? Path.Combine(folder, "external data 中文") : Path.Combine(root, "Data");
        Directory.CreateDirectory(Path.Combine(data, "notes")); Directory.CreateDirectory(Path.Combine(data, "assets"));
        File.WriteAllText(Path.Combine(data, "notes", "content.txt"), "original notes 中文");
        File.WriteAllBytes(Path.Combine(data, "assets", "image.png"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(root, "custom.txt"), "user owned file");
        File.WriteAllText(Path.Combine(root, "test-key.txt"), "Cut.Tests." + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(folder, "Cut-0.2.0-win-x64-portable"); ModernPayload(payload, "0.2.0", behavior);
        var zip = payload + ".zip"; ZipFile.CreateFromDirectory(payload, zip, CompressionLevel.Fastest, true);
        var store = new LauncherStore(root); _ = store.ReadState();
        DurableFiles.Write(store.CurrentFile, new LauncherState("bootstrap", null));
        DurableFiles.Write(store.QueueFile, new UpdateRequest(root, data, zip, DurableFiles.Hash(zip), "0.2.0", int.MaxValue, 0));
        return (root, data, store);
    }
    private static Process StartLauncherWorker(string root, string data, string checkpoint = "none", string? signal = null)
        => Process.Start(TestChild("--launcher-worker", root, data, checkpoint, signal ?? Path.Combine(root, "checkpoint.signal")))!;
    private static void WaitFile(string path, Process? process = null, int milliseconds = 30000)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (process?.HasExited == true) throw new InvalidOperationException("Worker exited " + process.ExitCode + " before " + path
                + (File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "worker-error.txt")) ? File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "worker-error.txt")) : ""));
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException("Timed out waiting for " + path);
            Thread.Sleep(20);
        }
    }
    private static void WaitWorker(Process process, string root)
    {
        if (!process.WaitForExit(45000)) { process.Kill(true); throw new TimeoutException("Launcher worker hung: " + root); }
        Check(process.ExitCode == 0, "Launcher worker failed: " + (File.Exists(Path.Combine(root, "worker-error.txt")) ? File.ReadAllText(Path.Combine(root, "worker-error.txt")) : process.ExitCode));
    }
    private static void StopProbeChildren(string root)
    {
        var folder = Path.Combine(root, "test-children");
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            try
            {
                using var child = Process.GetProcessById(doc.RootElement.GetProperty("pid").GetInt32());
                if (child.StartTime.ToUniversalTime().Ticks == doc.RootElement.GetProperty("started").GetInt64())
                { child.Kill(true); child.WaitForExit(5000); }
            }
            catch (ArgumentException) { }
        }
    }
    private static void AssertOriginalNotes(string data)
    {
        Check(File.ReadAllText(Path.Combine(data, "notes", "content.txt")) == "original notes 中文", "Recovery changed original note bytes.");
        Check(File.ReadAllBytes(Path.Combine(data, "assets", "image.png")).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Recovery changed image bytes.");
        Check(!File.Exists(Path.Combine(data, "trial-only.txt")), "Recovery retained an uncommitted data mutation.");
    }
    private static void LauncherSuccessAndFailures()
    {
        foreach (var behavior in new[] { "good", "crash", "hang", "ready-crash" })
        {
            var fixture = LauncherFixture("launcher-health-" + behavior, behavior, externalData: behavior == "hang");
            var launcherHash = DurableFiles.Hash(Path.Combine(fixture.Root, "Cut.exe"));
            try
            {
                using var worker = StartLauncherWorker(fixture.Root, fixture.Data); WaitWorker(worker, fixture.Root);
                WaitFile(Path.Combine(fixture.Root, "test-active.json"));
                var state = fixture.Store.ReadState();
                Check((state.Active == "bootstrap") == (behavior != "good"), "Startup failure did not select the confirmed version: " + behavior);
                Check(behavior == "good" ? state.Previous == "bootstrap" : state.RejectedPackages?.Length == 1, "Confirmed fallback or rejected package was lost.");
                Check(!DurableFiles.Exists(fixture.Store.PendingFile) && !DurableFiles.Exists(fixture.Store.QueueFile), "Finished transaction was left pending.");
                Check(DurableFiles.Hash(Path.Combine(fixture.Root, "Cut.exe")) == launcherHash && File.ReadAllText(Path.Combine(fixture.Root, "custom.txt")) == "user owned file", "Update replaced the stable launcher or personal file.");
                AssertOriginalNotes(fixture.Data);
            }
            finally { StopProbeChildren(fixture.Root); }
        }
    }
    private static void LauncherForcedInterruptionMatrix()
    {
        foreach (var phase in new[] { "staging-start", "staged", "published-slot", "snapshot-start", "snapshot-file", "snapshotted", "child-started", "initializing", "ready", "pointer-written", "committed", "activated" })
        {
            var fixture = LauncherFixture("launcher-kill-" + phase, "mutate");
            var signal = Path.Combine(fixture.Root, "checkpoint.signal");
            var launcherHash = DurableFiles.Hash(Path.Combine(fixture.Root, "Cut.exe"));
            try
            {
                using (var worker = StartLauncherWorker(fixture.Root, fixture.Data, phase, signal))
                {
                    try
                    {
                        WaitFile(signal, worker);
                        // Deliberately do NOT kill the tree: the Windows job must contain an unconfirmed child.
                        worker.Kill(); worker.WaitForExit(5000);
                    }
                    finally { if (!worker.HasExited) { worker.Kill(true); worker.WaitForExit(); } }
                }
                if (phase != "activated")
                {
                    var pending = DurableFiles.Read<UpdateTransaction>(fixture.Store.PendingFile);
                    if (pending.ChildPid != 0)
                    {
                        try { using var child = Process.GetProcessById(pending.ChildPid); Check(child.WaitForExit(4000), "Killing the launcher left an unconfirmed child alive."); }
                        catch (ArgumentException) { }
                    }
                }
                else
                {
                    WaitFile(Path.Combine(fixture.Root, "test-active.json"));
                    File.WriteAllText(Path.Combine(fixture.Data, "notes", "content.txt"), "user edit after commit");
                    // The independent commit record must prevent stale snapshot restoration even if the pointer tears.
                    File.WriteAllText(fixture.Store.CurrentFile, "{damaged pointer after activation");
                }
                using (var recovery = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(recovery, fixture.Root);
                WaitFile(Path.Combine(fixture.Root, "test-active.json"));
                var committed = phase is "pointer-written" or "committed" or "activated";
                Check((fixture.Store.ReadState().Active != "bootstrap") == committed, "Wrong version after interruption at " + phase);
                Check(!DurableFiles.Exists(fixture.Store.PendingFile), "Recovery did not clear the transaction at " + phase);
                if (!committed) AssertOriginalNotes(fixture.Data);
                if (phase == "activated") Check(File.ReadAllText(Path.Combine(fixture.Data, "notes", "content.txt")) == "user edit after commit", "Recovery overwrote an edit made after commit.");
                Check(DurableFiles.Hash(Path.Combine(fixture.Root, "Cut.exe")) == launcherHash, "Interrupted update damaged the stable launcher.");
            }
            finally { StopProbeChildren(fixture.Root); }
        }
    }
    private static void LauncherInterruptedRecovery()
    {
        var fixture = LauncherFixture("launcher-interrupted-recovery", "mutate", externalData: true);
        try
        {
            var firstSignal = Path.Combine(fixture.Root, "first.signal");
            using (var worker = StartLauncherWorker(fixture.Root, fixture.Data, "ready", firstSignal))
            { WaitFile(firstSignal, worker); worker.Kill(); worker.WaitForExit(5000); }
            var secondSignal = Path.Combine(fixture.Root, "second.signal");
            using (var recovery = StartLauncherWorker(fixture.Root, fixture.Data, "restore-file", secondSignal))
            { WaitFile(secondSignal, recovery); recovery.Kill(); recovery.WaitForExit(5000); }
            using (var recovery = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(recovery, fixture.Root);
            AssertOriginalNotes(fixture.Data);
            Check(fixture.Store.ReadState().Active == "bootstrap" && !DurableFiles.Exists(fixture.Store.PendingFile), "Interrupted recovery did not converge.");
        }
        finally { StopProbeChildren(fixture.Root); }
    }
    private static void LauncherCorruptionAndSnapshots()
    {
        var fixture = LauncherFixture("launcher-snapshot-guard", "mutate");
        try
        {
            var signal = Path.Combine(fixture.Root, "checkpoint.signal");
            using (var worker = StartLauncherWorker(fixture.Root, fixture.Data, "ready", signal))
            { WaitFile(signal, worker); worker.Kill(); worker.WaitForExit(5000); }
            var pending = DurableFiles.Read<UpdateTransaction>(fixture.Store.PendingFile);
            var protectedNote = Path.Combine(fixture.Store.Snapshot(pending), "files", "notes", "content.txt");
            var original = File.ReadAllBytes(protectedNote);
            File.WriteAllText(protectedNote, "damaged snapshot");
            using (var worker = StartLauncherWorker(fixture.Root, fixture.Data))
            { Check(worker.WaitForExit(12000) && worker.ExitCode != 0, "Damaged snapshot did not stop recovery."); }
            Check(File.ReadAllText(Path.Combine(fixture.Data, "notes", "content.txt")) == "candidate initialization" && DurableFiles.Exists(fixture.Store.PendingFile), "Failed snapshot validation wrote data or discarded recovery state.");
            File.WriteAllBytes(protectedNote, original);
            using (var worker = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(worker, fixture.Root);
            AssertOriginalNotes(fixture.Data);
            StopProbeChildren(fixture.Root);
            // A corrupt primary pointer can use the previous durable generation; no directory guessing is performed.
            var before = fixture.Store.ReadState(); DurableFiles.Write(fixture.Store.CurrentFile, before);
            File.WriteAllText(fixture.Store.CurrentFile, "{torn");
            Check(fixture.Store.ReadState().Active == "bootstrap", "Previous pointer generation was not recoverable.");
            foreach (var file in new[] { fixture.Store.CurrentFile, fixture.Store.CurrentFile + ".bak", Path.Combine(fixture.Root, "release-manifest.json") })
            {
                // Windows PowerShell 5 writes UTF-8 with BOM; state/manual repairs and older packages must accept it.
                var text = file == fixture.Store.CurrentFile ? JsonSerializer.Serialize(before, DurableFiles.Json) : File.ReadAllText(file);
                File.WriteAllText(file, text, new UTF8Encoding(true));
            }
            Check(fixture.Store.ReadState().Active == "bootstrap" && VersionLayout.Validate(fixture.Root) == "0.1.0", "UTF-8 BOM made a valid pointer or package unreadable.");
        }
        finally { StopProbeChildren(fixture.Root); }
    }

    private static void LauncherLateFailurePreservesNewEdits()
    {
        var fixture = LauncherFixture("launcher-late-startup-failure");
        try
        {
            using (var first = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(first, fixture.Root);
            WaitFile(Path.Combine(fixture.Root, "test-active.json")); StopProbeChildren(fixture.Root);
            var state = fixture.Store.ReadState();
            File.WriteAllText(Path.Combine(fixture.Data, "notes", "content.txt"), "new edit after successful upgrade");
            File.WriteAllText(DurableFiles.Child(VersionLayout.Slot(fixture.Root, state.Active), VersionLayout.Runtime), "tampered program");
            using (var next = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(next, fixture.Root);
            Check(fixture.Store.ReadState().Active == "bootstrap", "A confirmed version that could no longer launch did not fall back.");
            Check(File.ReadAllText(Path.Combine(fixture.Data, "notes", "content.txt")) == "new edit after successful upgrade", "Late rollback restored a stale data snapshot over new edits.");
            Check(fixture.Store.ReadState().RejectedPackages?.Length == 1, "Late failed version was not quarantined.");
        }
        finally { StopProbeChildren(fixture.Root); }
    }

    private static void LauncherInvalidCandidates()
    {
        foreach (var scenario in new[] { "schema", "protocol", "component", "snapshot-lock" })
        {
            var fixture = LauncherFixture("launcher-invalid-" + scenario);
            FileStream? locked = null;
            try
            {
                if (scenario == "snapshot-lock")
                    locked = new FileStream(Path.Combine(fixture.Data, "notes", "content.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                else
                {
                    var request = DurableFiles.Read<UpdateRequest>(fixture.Store.QueueFile);
                    using (var zip = ZipFile.Open(request.Package, ZipArchiveMode.Update))
                    {
                        var entry = zip.GetEntry("Cut-0.2.0-win-x64-portable/release-manifest.json")!;
                        System.Text.Json.Nodes.JsonNode manifest;
                        using (var reader = new StreamReader(entry.Open())) manifest = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!;
                        entry.Delete();
                        if (scenario == "schema") manifest["dataSchemaVersion"] = 2;
                        if (scenario == "protocol") manifest["launcherProtocol"] = 9;
                        if (scenario == "component") manifest["runtimeSha256"] = new string('0', 64);
                        using var writer = new StreamWriter(zip.CreateEntry("Cut-0.2.0-win-x64-portable/release-manifest.json").Open());
                        writer.Write(manifest.ToJsonString());
                    }
                    DurableFiles.Write(fixture.Store.QueueFile, request with { Sha256 = DurableFiles.Hash(request.Package) });
                }
                using (var worker = StartLauncherWorker(fixture.Root, fixture.Data)) WaitWorker(worker, fixture.Root);
                Check(fixture.Store.ReadState().Active == "bootstrap" && !DurableFiles.Exists(fixture.Store.PendingFile), "Unsafe candidate changed the confirmed version.");
                locked?.Dispose(); locked = null;
                AssertOriginalNotes(fixture.Data);
            }
            finally { locked?.Dispose(); StopProbeChildren(fixture.Root); }
        }
    }
}
