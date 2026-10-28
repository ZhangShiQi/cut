using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using Cut.UpdateEngine;

namespace Cut.UpdateEngine;

public static class VersionLayout
{
    public const int Protocol = 1;
    public const int DataSchema = 1;
    public const string Runtime = "tools/runtime/Cut.exe";
    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)
            || !Version.TryParse(value, out var version)) throw new InvalidDataException("无效的正式版本号。");
        return version;
    }
    public static string Slot(string root, string id)
    {
        if (id == "bootstrap") return Path.GetFullPath(root);
        if (Regex.IsMatch(id, @"^legacy-[a-f0-9]{32}$")) return DurableFiles.Child(root, ".cut-update-backup-" + id[7..]);
        if (!Regex.IsMatch(id, @"^[0-9]+\.[0-9]+\.[0-9]+-[a-f0-9]{32}$")) throw new InvalidDataException("无效的版本目录。");
        return DurableFiles.Child(root, "versions/" + id);
    }
    public static string Validate(string directory, string? expectedVersion = null, int maxParallelism = 4)
    {
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        UpdatePackage.RejectReparseAncestors(directory);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release-manifest.json")));
        var m = doc.RootElement;
        var version = m.GetProperty("version").GetString()!;
        ParseVersion(version);
        if (expectedVersion is not null && version != expectedVersion) throw new InvalidDataException("版本目录与发布清单不匹配。");
        if (m.GetProperty("application").GetString() != "Cut" || m.GetProperty("runtimeIdentifier").GetString() != "win-x64"
            || !m.GetProperty("selfContained").GetBoolean() || m.GetProperty("singleFile").GetBoolean()
            || m.GetProperty("formatVersion").GetInt32() != 2 || m.GetProperty("launcherProtocol").GetInt32() != Protocol
            || m.GetProperty("dataSchemaVersion").GetInt32() != DataSchema || m.GetProperty("runtimePath").GetString() != Runtime)
            throw new InvalidDataException("更新包与稳定启动器或数据格式不兼容，请保留 Data 并手动升级启动器。");
        var files = m.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Object) throw new InvalidDataException("文件清单无效。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Name, string Expected)>();
        foreach (var file in files.EnumerateObject())
        {
            if (!seen.Add(file.Name) || file.Name == "release-manifest.json"
                || !UpdatePackage.ManagedEntries.Contains(file.Name.Split('/')[0], StringComparer.Ordinal)) throw new InvalidDataException("无效的程序文件清单。");
            var expected = file.Value.GetString();
            if (expected is null || !Regex.IsMatch(expected, "^[a-f0-9]{64}$")) throw new InvalidDataException("程序组件校验失败：" + file.Name);
            entries.Add((file.Name, expected));
        }
        // Stream files through a bounded number of workers; never read entire bundles into memory.
        // Keep manifest validation and the final decision on this thread before any child can start.
        var hashes = new string[entries.Count];
        try
        {
            Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, maxParallelism) }, i =>
            {
                var entry = entries[i];
                var actual = DurableFiles.Hash(DurableFiles.Child(directory, entry.Name));
                if (actual != entry.Expected) throw new InvalidDataException("程序组件校验失败：" + entry.Name);
                hashes[i] = actual;
            });
        }
        catch (AggregateException ex) { ExceptionDispatchInfo.Capture(ex.Flatten().InnerExceptions[0]).Throw(); throw; }
        var actualHashes = entries.Select((entry, i) => (entry.Name, Hash: hashes[i])).ToDictionary(x => x.Name, x => x.Hash, StringComparer.OrdinalIgnoreCase);
        foreach (var managed in UpdatePackage.ManagedEntries.Where(e => e != "release-manifest.json"))
        {
            var path = Path.Combine(directory, managed);
            if (Directory.Exists(path)) VerifyDirectory(path);
            else if (File.Exists(path) && !seen.Contains(managed)) throw new InvalidDataException("程序文件未列入清单：" + managed);
        }
        void VerifyDirectory(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                UpdatePackage.RejectReparseAncestors(child);
                if (Directory.Exists(child)) VerifyDirectory(child);
                else if (!seen.Contains(Path.GetRelativePath(directory, child).Replace('\\', '/')))
                    throw new InvalidDataException("程序文件未列入清单。");
            }
        }
        foreach (var required in new[] { "Cut.exe", Runtime, "portable.flag", "tools/runtime/Models/manifest.json", "tools/runtime/Models/ch_PP-OCRv5_det_mobile.onnx", "tools/runtime/Models/ch_PP-OCRv5_rec_mobile.onnx", "tools/runtime/onnxruntime.dll", "tools/runtime/OpenCvSharpExtern.dll" })
            if (!seen.Contains(required)) throw new InvalidDataException("更新包缺少受校验的组件：" + required);
        if (actualHashes["Cut.exe"] != m.GetProperty("executableSha256").GetString()
            || actualHashes[Runtime] != m.GetProperty("runtimeSha256").GetString())
            throw new InvalidDataException("启动器或运行程序校验失败。");
        return version;
    }
}
