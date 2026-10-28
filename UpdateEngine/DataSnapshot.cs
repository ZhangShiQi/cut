using Cut.UpdateEngine;

namespace Cut.UpdateEngine;

public sealed record SnapshotIndex(Dictionary<string, string> Files);

public static class DataSnapshot
{
    private static bool Excluded(string relative) => relative.Equals("updates", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("updates/", StringComparison.OrdinalIgnoreCase) || relative.Equals("device", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("device/", StringComparison.OrdinalIgnoreCase);
    private static IEnumerable<string> Files(string root, string relative = "")
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var key = relative + Path.GetFileName(path);
            if (Excluded(key)) continue;
            UpdatePackage.RejectReparseAncestors(path);
            if (Directory.Exists(path)) { foreach (var child in Files(path, key + "/")) yield return child; }
            else yield return key;
        }
    }
    public static void Create(string data, string destination, Action<string>? checkpoint = null)
    {
        UpdatePackage.RejectReparseAncestors(data);
        if (Directory.Exists(destination)) throw new IOException("数据快照目录已存在。");
        var paths = Files(data).ToArray();
        UpdatePackage.CheckSpace(destination, paths.Sum(relative => new FileInfo(DurableFiles.Child(data, relative)).Length) + 16L * 1024 * 1024);
        Directory.CreateDirectory(destination);
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in paths)
        {
            var source = DurableFiles.Child(data, relative);
            var output = DurableFiles.Child(destination, "files/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using (var input = File.OpenRead(source))
            using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            { input.CopyTo(target); target.Flush(true); }
            index.Add(relative, DurableFiles.Hash(output));
            checkpoint?.Invoke("snapshot-file");
        }
        DurableFiles.Write(Path.Combine(destination, "index.json"), new SnapshotIndex(index));
    }
    public static void Restore(string data, string snapshot, Action<string>? checkpoint = null)
    {
        var index = DurableFiles.Read<SnapshotIndex>(Path.Combine(snapshot, "index.json"));
        // Check the complete snapshot BEFORE changing any original file.
        foreach (var (relative, hash) in index.Files)
        {
            if (Excluded(relative) || DurableFiles.Hash(DurableFiles.Child(snapshot, "files/" + relative)) != hash)
                throw new IOException("数据快照校验失败，保留现场并停止恢复。");
            _ = DurableFiles.Child(data, relative);
        }
        foreach (var (relative, hash) in index.Files)
        {
            var target = DurableFiles.Child(data, relative);
            if (!File.Exists(target) || DurableFiles.Hash(target) != hash)
            {
                // No .bak here: the protected snapshot is the recovery source, not a partly restored file.
                var bytes = File.ReadAllBytes(DurableFiles.Child(snapshot, "files/" + relative));
                DurableFiles.WriteBytes(target, bytes, backup: false);
            }
            checkpoint?.Invoke("restore-file");
        }
        foreach (var relative in Files(data).ToArray())
            if (!index.Files.ContainsKey(relative)) File.Delete(DurableFiles.Child(data, relative));
        foreach (var (relative, hash) in index.Files)
            if (DurableFiles.Hash(DurableFiles.Child(data, relative)) != hash) throw new IOException("数据恢复后的校验失败。");
    }
}
