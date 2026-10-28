using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

using Cut.UpdateEngine;

namespace Cut.UpdateEngine;

public sealed class LauncherOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan StabilityTime { get; init; } = TimeSpan.FromSeconds(2);
    // Injected only by the test harness; the shipped launcher always uses real runtime executables and the user/session singleton.
    public string? InstanceKey { get; init; }
    public Func<string, ProcessStartInfo>? RuntimeCommand { get; init; }
    public Action<string>? Checkpoint { get; init; }
    public bool AllowReinstallForTests { get; init; }
}

public sealed class LauncherEngine
{
    private readonly LauncherStore _store;
    private readonly LauncherOptions _options;
    private SingleInstanceHost? _reservation;
    public LauncherEngine(string root, LauncherOptions? options = null)
    { _store = new LauncherStore(root); _options = options ?? new(); }
    private void Point(string name) { StartupTiming.Mark(name); _options.Checkpoint?.Invoke(name); }

    public int Run(string data, bool startup = false)
    {
        data = Path.GetFullPath(data);
        ValidateData(data);
        // No Mutex awaits: the calling OS thread acquires and releases each mutex.
        using var mutex = new Mutex(false, @"Local\" + _store.MutexKey);
        bool owns;
        try { owns = mutex.WaitOne(TimeSpan.FromSeconds(130)); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) throw new IOException("另一个启动器仍在处理更新，请稍后重试。");
        try
        {
            var state = _store.ReadState();
            if (DurableFiles.Exists(_store.PendingFile))
            {
                var pending = DurableFiles.Read<UpdateTransaction>(_store.PendingFile);
                ValidateData(pending.Data);
                data = Path.GetFullPath(pending.Data);
                state = ResolveCommit(state, pending);
                if (Committed(state, pending)) state = Recover(state);
                else
                {
                    StopRecorded(pending);
                    if (!Reserve()) return 0;
                    state = Recover(state);
                }
            }
            if (_reservation is null && !Reserve()) return 0;
            if (!state.Initialized)
            {
                try
                {
                    var trial = new UpdateTransaction(Guid.NewGuid().ToString("N"), "bootstrap", data,
                        DurableFiles.Hash(DurableFiles.Child(_store.Root, VersionLayout.Runtime)), state.Revision, "snapshot");
                    DurableFiles.Write(_store.PendingFile, trial);
                    DataSnapshot.Create(data, _store.Snapshot(trial), _options.Checkpoint);
                    trial = trial with { SnapshotComplete = true, Phase = "trial" };
                    DurableFiles.Write(_store.PendingFile, trial);
                    Start(state, "bootstrap", data, startup, trial);
                    return 0;
                }
                catch
                {
                    if (_reservation is null && !Reserve()) throw;
                    state = Recover(_store.ReadState());
                    if (state.Previous is null) throw;
                    // No new code has become writable. Restore data before running the known older app.
                    state = state with { Active = state.Previous, Previous = null, Revision = state.Revision + 1, Initialized = true };
                    DurableFiles.Write(_store.CurrentFile, state);

                    Result(data, false, "新版首次启动失败，已恢复数据并启动旧版。旧版自动更新已暂停，请等待修复版本。");
                    StartConfirmed(state, data, startup);
                    return 0;
                }
            }
            if (DurableFiles.Exists(_store.QueueFile))
            {
                var request = DurableFiles.Read<UpdateRequest>(_store.QueueFile);
                try
                {
                    ValidateRequest(request);
                    data = Path.GetFullPath(request.Data); startup = request.Startup;
                    if (VersionLayout.ParseVersion(request.Version) > VersionLayout.ParseVersion(ReadVersion(state.Active)) || _options.AllowReinstallForTests)
                    {
                        var transaction = Stage(request, state);
                        Start(state, transaction.Candidate, data, startup, transaction);
                        return 0;
                    }
                    DurableFiles.Delete(_store.QueueFile);
                }
                catch (Exception ex)
                {
                    if (_reservation is null && !Reserve()) throw new IOException("更新恢复等待应用退出。", ex);
                    state = Recover(_store.ReadState());
                    DurableFiles.Delete(_store.QueueFile);
                    Result(data, false, "更新未完成，继续运行已确认版本：" + ex.Message);
                }
            }
            StartConfirmed(state, data, startup);
            return 0;
        }
        finally { _reservation?.Dispose(); _reservation = null; mutex.ReleaseMutex(); }
    }

    public static void WaitForQueuedParent(string root)
    {
        var store = new LauncherStore(root);
        if (!DurableFiles.Exists(store.QueueFile)) return;
        var request = DurableFiles.Read<UpdateRequest>(store.QueueFile);
        if (!Path.GetFullPath(request.Root).Equals(store.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新目标与启动器不匹配。");
        try
        {
            using var parent = Process.GetProcessById(request.ParentPid);
            if (parent.StartTime.ToUniversalTime().Ticks == request.ParentStarted && !parent.WaitForExit(120000))
                throw new IOException("等待主程序退出超时；当前版本未被替换。");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }
    private bool Reserve()
    {
        _reservation = new SingleInstanceHost(_options.InstanceKey);
        if (_reservation.IsPrimary) return true;
        _reservation.NotifyExistingAsync().GetAwaiter().GetResult();
        _reservation.Dispose(); _reservation = null;
        // Never recover data while a different application instance owns the lock.
        if (DurableFiles.Exists(_store.PendingFile)) throw new IOException("配置仍在运行，已暂停更新恢复。请退出后重新启动。");
        return false;
    }
    private void ValidateData(string data)
    {
        UpdatePackage.ValidateTarget(_store.Root, data);
        if (UpdatePackage.IsWithin(data, Path.Combine(_store.Root, "versions")) || UpdatePackage.IsWithin(data, _store.DirectoryPath)
            || UpdatePackage.IsWithin(_store.DirectoryPath, data)) throw new IOException("数据目录与启动器恢复目录重叠。");
        UpdatePackage.RejectReparseAncestors(data);
    }
    private void ValidateRequest(UpdateRequest request)
    {
        if (!Path.GetFullPath(request.Root).Equals(_store.Root, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新目标与启动器不匹配。");
        ValidateData(Path.GetFullPath(request.Data));
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Sha256, "^[a-f0-9]{64}$")) throw new IOException("无效的更新包摘要。");
        VersionLayout.ParseVersion(request.Version);
        if (LauncherStore.IsRejected(_store.Root, request.Sha256)) throw new IOException("此更新包曾启动失败，已停止重复安装；请等待修复版本。");
    }
    private string ReadVersion(string slot) => VersionLayout.Validate(VersionLayout.Slot(_store.Root, slot));
    private UpdateTransaction Stage(UpdateRequest request, LauncherState state)
    {
        var transaction = new UpdateTransaction(Guid.NewGuid().ToString("N"), request.Version + "-" + Guid.NewGuid().ToString("N"),
            Path.GetFullPath(request.Data), request.Sha256, state.Revision);
        DurableFiles.Write(_store.PendingFile, transaction);
        Point("staging-start");
        UpdatePackage.CheckSpace(_store.Root, new FileInfo(request.Package).Length + 64L * 1024 * 1024);
        UpdatePackage.VerifyHashAsync(request.Package, request.Sha256, CancellationToken.None).GetAwaiter().GetResult();
        var stage = _store.Stage(transaction);
        UpdatePackage.Extract(request.Package, stage, request.Version);
        VersionLayout.Validate(stage, request.Version); // Legacy packages are not supported by the new transaction protocol.
        Point("staged");
        var slot = VersionLayout.Slot(_store.Root, transaction.Candidate);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        DurableFiles.RetryIO(() => Directory.Move(stage, slot)); // Antivirus/indexers can briefly hold a newly scanned directory.
        Point("published-slot");
        transaction = transaction with { Phase = "snapshot" };
        DurableFiles.Write(_store.PendingFile, transaction);
        Point("snapshot-start");
        DataSnapshot.Create(transaction.Data, _store.Snapshot(transaction), _options.Checkpoint);
        transaction = transaction with { Phase = "trial", SnapshotComplete = true };
        DurableFiles.Write(_store.PendingFile, transaction);
        Point("snapshotted");
        return transaction;
    }
    private static bool Committed(LauncherState state, UpdateTransaction transaction) => state.Revision > transaction.BaseRevision && state.Active == transaction.Candidate;
    private LauncherState ResolveCommit(LauncherState state, UpdateTransaction transaction)
    {
        // An independent durable commit record prevents a damaged current.json from resurrecting
        // a pre-commit snapshot over edits made after the user was allowed to type.
        if (!DurableFiles.Exists(_store.Commit(transaction))) return state;
        var committed = DurableFiles.Read<LauncherState>(_store.Commit(transaction));
        if (!Committed(committed, transaction) || committed.ActivePackageHash != transaction.PackageHash)
            throw new IOException("版本提交记录与事务不匹配，保留现场并停止恢复。");
        if (committed.Revision > state.Revision)
        { DurableFiles.Write(_store.CurrentFile, committed); return committed; }
        return state;
    }
    private LauncherState Recover(LauncherState state)
    {
        if (!DurableFiles.Exists(_store.PendingFile)) return state;
        var transaction = DurableFiles.Read<UpdateTransaction>(_store.PendingFile);
        state = ResolveCommit(state, transaction);
        ValidateData(transaction.Data);
        _ = VersionLayout.Slot(_store.Root, transaction.Candidate);
        if (!Committed(state, transaction))
        {
            StopRecorded(transaction);
            if (transaction.SnapshotComplete)
            {
                DurableFiles.Write(_store.PendingFile, transaction with { Phase = "restoring" });
                DataSnapshot.Restore(transaction.Data, _store.Snapshot(transaction), _options.Checkpoint);
                Point("restored");
                state = state with { RejectedPackages = (state.RejectedPackages ?? []).Append(transaction.PackageHash).Distinct().ToArray() };
                DurableFiles.Write(_store.CurrentFile, state);
            }
            Result(transaction.Data, false, "上次更新未完成，已恢复启动前数据，继续运行原版本。未确认版本已隔离。");
        }
        DurableFiles.Delete(_store.QueueFile);
        DurableFiles.Delete(_store.PendingFile);
        Point("recovery-complete");
        return state;
    }
    private void StartConfirmed(LauncherState state, string data, bool startup)
    {
        try { StartSlot(state, state.Active, data, startup); }
        catch (Exception ex)
        {
            if (state.Previous is null) throw new IOException("已确认版本也无法启动；Data 和所有版本目录已保留。" + ex.Message, ex);
            if (_reservation is null && !Reserve()) throw;
            _ = ReadVersion(state.Previous);
            var fallback = state with { Active = state.Previous, Previous = null, Revision = state.Revision + 1,
                ActivePackageHash = state.PreviousPackageHash, PreviousPackageHash = null,
                RejectedPackages = state.ActivePackageHash is null ? state.RejectedPackages : (state.RejectedPackages ?? []).Append(state.ActivePackageHash).Distinct().ToArray() };
            DurableFiles.Write(_store.CurrentFile, fallback);
            Result(data, false, "当前版本启动失败，已退回上一个已确认版本；保留现有配置内容。" + ex.Message);
            StartSlot(fallback, fallback.Active, data, startup);
        }
    }
    private void StartSlot(LauncherState state, string slot, string data, bool startup) => Start(state, slot, data, startup, null);
    private void Start(LauncherState state, string slotId, string data, bool startup, UpdateTransaction? transaction)
    {
        var slot = VersionLayout.Slot(_store.Root, slotId);
        Point("validation-start");
        VersionLayout.Validate(slot);
        Point("validation-complete");
        var runtime = DurableFiles.Child(slot, VersionLayout.Runtime);
        var pipeName = "Cut.Startup." + Guid.NewGuid().ToString("N");
        var nonce = Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var info = _options.RuntimeCommand?.Invoke(runtime) ?? new ProcessStartInfo(runtime);
        info.UseShellExecute = false; info.CreateNoWindow = true; info.WorkingDirectory = slot;
        foreach (var arg in new[] { "--data-dir", data, "--launcher-root", _store.Root, "--version-root", slot,
            "--startup-pipe", pipeName, "--startup-nonce", nonce }) info.ArgumentList.Add(arg);
        if (_options.InstanceKey is not null) { info.ArgumentList.Add("--test-instance-key"); info.ArgumentList.Add(_options.InstanceKey); }
        if (startup) info.ArgumentList.Add("--autostart");
        using var child = Process.Start(info) ?? throw new IOException("无法启动配置程序。");
        ChildLifetime? lifetime = null;
        var committed = false;
        try
        {
            lifetime = new ChildLifetime(child);
            if (transaction is not null)
            {
                transaction = transaction with { ChildPid = child.Id, ChildStarted = child.StartTime.ToUniversalTime().Ticks,
                    ChildImage = Path.GetFullPath(info.FileName), Phase = "starting" };
                DurableFiles.Write(_store.PendingFile, transaction);
            }
            Point("child-started");
            using var timeout = new CancellationTokenSource(_options.StartupTimeout);
            child.EnableRaisingEvents = true;
            child.Exited += (_, _) => { try { timeout.Cancel(); } catch (ObjectDisposedException) { } };
            if (child.HasExited) timeout.Cancel();
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            if (reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != nonce + ":" + child.Id) throw new IOException("启动确认身份不匹配。");
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid) || clientPid != child.Id)
                throw new IOException("启动管道连接进程不匹配。");
            // No app may initialize data before its identity has been durably recorded.
            _reservation?.Dispose(); _reservation = null;
            writer.WriteLine("initialize");
            Point("initializing");
            if (reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult() != "ready") throw new IOException("新版本未完成启动确认。");
            Point("ready");
            // New/unconfirmed code must remain read-only throughout the rollback window.
            if (transaction is not null && child.WaitForExit((int)_options.StabilityTime.TotalMilliseconds)) throw new IOException("程序在启动确认后退出。");
            if (child.HasExited) throw new IOException("程序在启动确认前退出。");
            if (transaction is not null)
            {
                var confirmed = state with { Active = slotId, Previous = state.Active == slotId ? state.Previous : state.Active, Revision = state.Revision + 1,
                    ActivePackageHash = transaction.PackageHash, PreviousPackageHash = state.ActivePackageHash, Initialized = true };
                DurableFiles.Write(_store.CurrentFile, confirmed);
                Point("pointer-written");
                DurableFiles.Write(_store.Commit(transaction), confirmed);
            }
            Point("committed");
            // Durable pointer first. If killed before 'commit', the next launch retries this confirmed slot.
            lifetime.Release();
            writer.WriteLine("commit");
            committed = true;
            Point("activated");
            if (transaction is not null)
            {
                Result(data, true, "新版已通过界面初始化确认；旧版和升级前数据快照均已保留。");
                DurableFiles.Delete(_store.QueueFile);
                DurableFiles.Delete(_store.PendingFile);
            }
            else
            {
                // Confirmed code can become interactive immediately. Still observe late crashes and
                // fall back without restoring a stale data snapshot over edits made in this session.
                Point("observing-confirmed");
                if (child.WaitForExit((int)_options.StabilityTime.TotalMilliseconds) && child.ExitCode != 0)
                    throw new IOException("已确认程序在启动后异常退出。");
            }
        }
        finally
        {
            if (!committed)
            {
                try { if (!child.HasExited) child.Kill(true); if (!child.WaitForExit(10000)) throw new IOException("未确认程序未退出，暂停恢复。"); }
                finally { lifetime?.Dispose(); }
            }
            else lifetime?.Dispose();
        }
    }
    private void StopRecorded(UpdateTransaction transaction)
    {
        if (transaction.ChildPid == 0) return;
        try
        {
            using var child = Process.GetProcessById(transaction.ChildPid);
            if (child.StartTime.ToUniversalTime().Ticks != transaction.ChildStarted) return;
            var expected = _options.RuntimeCommand is null ? DurableFiles.Child(VersionLayout.Slot(_store.Root, transaction.Candidate), VersionLayout.Runtime) : transaction.ChildImage;
            if (expected is null || !string.Equals(child.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新子进程身份不符，停止恢复以保护当前会话。");
            child.Kill(true);
            if (!child.WaitForExit(10000)) throw new IOException("未确认程序仍在运行，停止数据恢复。");
        }
        catch (ArgumentException) { }
    }
    private static void Result(string data, bool success, string message)
    {
        try { DurableFiles.Write(Path.Combine(data, "device", "update-result.json"), new { success, message, time = DateTimeOffset.UtcNow }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Do not turn a diagnostic failure into a second install. */ }
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(nint pipe, out uint processId);
}
