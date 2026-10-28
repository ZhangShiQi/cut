using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cut.UpdateEngine;

namespace Cut.UpdateEngine;

public sealed record LauncherState(string Active, string? Previous, long Revision = 0, string[]? RejectedPackages = null,
    string? ActivePackageHash = null, string? PreviousPackageHash = null, bool Initialized = true);
public sealed record UpdateRequest(string Root, string Data, string Package, string Sha256, string Version, int ParentPid, long ParentStarted, bool Startup = false);
public sealed record UpdateTransaction(string Id, string Candidate, string Data, string PackageHash, long BaseRevision,
    string Phase = "staging", bool SnapshotComplete = false, int ChildPid = 0, long ChildStarted = 0, string? ChildImage = null);

public sealed class LauncherStore
{
    public string Root { get; }
    public string DirectoryPath => Path.Combine(Root, ".cut-update");
    public string CurrentFile => Path.Combine(DirectoryPath, "current.json");
    public string PendingFile => Path.Combine(DirectoryPath, "pending.json");
    public string QueueFile => Path.Combine(DirectoryPath, "queued.json");
    public LauncherStore(string root)
    {
        Root = Path.GetFullPath(root);
        UpdatePackage.RejectReparseAncestors(DirectoryPath);
    }
    public string Snapshot(UpdateTransaction transaction) => DurableFiles.Child(DirectoryPath, "snapshots/" + SafeId(transaction.Id));
    public string Stage(UpdateTransaction transaction) => DurableFiles.Child(DirectoryPath, "staging/" + SafeId(transaction.Id));
    public string Commit(UpdateTransaction transaction) => DurableFiles.Child(DirectoryPath, "commits/" + SafeId(transaction.Id) + ".json");
    private static string SafeId(string id) => Guid.TryParseExact(id, "N", out _) ? id : throw new InvalidDataException("无效的更新事务编号。");
    public LauncherState ReadState()
    {
        if (DurableFiles.Exists(CurrentFile))
        {
            var state = DurableFiles.Read<LauncherState>(CurrentFile);
            _ = VersionLayout.Slot(Root, state.Active);
            if (state.Previous is not null) _ = VersionLayout.Slot(Root, state.Previous);
            return state;
        }
        VersionLayout.Validate(Root);
        var initial = new LauncherState("bootstrap", null, Initialized: false);
        DurableFiles.Write(CurrentFile, initial);
        return initial;
    }
    public static bool IsManaged(string root) => File.Exists(Path.Combine(root, "Cut.exe")) && File.Exists(Path.Combine(root, "release-manifest.json"));
    public static bool IsRejected(string root, string hash) => DurableFiles.Exists(new LauncherStore(root).CurrentFile)
        && (DurableFiles.Read<LauncherState>(new LauncherStore(root).CurrentFile).RejectedPackages ?? []).Contains(hash, StringComparer.OrdinalIgnoreCase);
    public static void Queue(UpdateRequest request)
    {
        var store = new LauncherStore(request.Root);
        UpdatePackage.ValidateTarget(store.Root, request.Data);
        if (UpdatePackage.IsWithin(request.Data, Path.Combine(store.Root, "versions")) || UpdatePackage.IsWithin(request.Data, store.DirectoryPath)
            || UpdatePackage.IsWithin(store.DirectoryPath, request.Data))
            throw new IOException("Data 不能位于启动器状态或版本目录内。");
        VersionLayout.ParseVersion(request.Version);
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Sha256, "^[a-f0-9]{64}$")) throw new InvalidDataException("无效的更新摘要。");
        if (DurableFiles.Exists(store.QueueFile) || DurableFiles.Exists(store.PendingFile)) throw new IOException("已有更新等待处理，请先退出并重新启动 Cut。");
        DurableFiles.Write(store.QueueFile, request);
        try
        {
            var info = new ProcessStartInfo(Path.Combine(store.Root, "Cut.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = store.Root, ArgumentList = { "--finish-update" } };
            if (UpdatePaths.TestKey is not null)
                foreach (var arg in new[] { "--data-dir", request.Data, "--test-instance-key", UpdatePaths.TestKey }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null) throw new IOException("无法启动稳定启动器。");
        }
        catch { DurableFiles.Delete(store.QueueFile); throw; }
    }
    public string MutexKey => "Cut.Launcher." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
}
