using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cut.UpdateEngine;

public static class UpdatePackage
{
    public static readonly string[] ManagedEntries = ["Cut.exe", "tools", "licenses", "assets", "release-manifest.json", "portable.flag", "使用说明.txt"];
    public static async Task VerifyHashAsync(string path, string expected, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        // The standalone helper calls this synchronously during WPF startup. Never resume on its dispatcher.
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA256 校验失败，未安装。");
    }
    public static string Extract(string archive, string destination, string version)
    {
        Cut.UpdateEngine.VersionLayout.ParseVersion(version);
        if (Directory.Exists(destination)) throw new IOException("更新暂存目录已存在。");
        using var zip = ZipFile.OpenRead(archive);
        var prefix = $"Cut-{version}-win-x64-portable/";
        var entries = new List<(ZipArchiveEntry Entry, string Relative)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == prefix) continue;
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.Contains('\\')) throw new InvalidDataException("更新包根目录无效。");
            var relative = entry.FullName[prefix.Length..];
            var parts = relative.TrimEnd('/').Split('/');
            if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                throw new InvalidDataException("更新包包含不安全的路径。");
            var isFolder = relative.EndsWith('/');
            if (!ManagedEntries.Contains(parts[0], StringComparer.Ordinal)
                || (parts.Length > 1 && parts[0] is not ("tools" or "licenses" or "assets"))
                || (parts.Length == 1 && !isFolder && parts[0] is "tools" or "licenses" or "assets"))
                throw new InvalidDataException("更新包包含非程序文件，拒绝覆盖数据。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包不允许符号链接。");
            if (!seen.Add(relative.TrimEnd('/'))) throw new InvalidDataException("更新包包含重复路径。");
            expanded = checked(expanded + entry.Length);
            if (expanded > 1536L * 1024 * 1024 || seen.Count > 25000) throw new InvalidDataException("更新包展开大小超出限制。");
            entries.Add((entry, relative));
        }
        CheckSpace(destination, expanded + 64L * 1024 * 1024);
        RejectReparseAncestors(destination);
        Directory.CreateDirectory(destination);
        foreach (var (entry, relative) in entries)
        {
            var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (relative.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
        foreach (var required in new[] { "Cut.exe", "release-manifest.json", "portable.flag", VersionLayout.Runtime })
            if (!File.Exists(Path.Combine(destination, required))) throw new InvalidDataException("更新包缺少必要的程序组件。");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "release-manifest.json")));
        var m = manifest.RootElement;
        if (m.GetProperty("application").GetString() != "Cut" || m.GetProperty("version").GetString() != version
            || m.GetProperty("runtimeIdentifier").GetString() != "win-x64" || !m.GetProperty("selfContained").GetBoolean()
            || m.GetProperty("singleFile").GetBoolean()) throw new InvalidDataException("更新包版本或运行平台不匹配。");
        using var executable = File.OpenRead(Path.Combine(destination, "Cut.exe"));
        var exeHash = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        if (m.GetProperty("executableSha256").GetString() != exeHash) throw new InvalidDataException("更新程序校验失败。");
        if (m.TryGetProperty("formatVersion", out var format) && format.GetInt32() == 2)
            Cut.UpdateEngine.VersionLayout.Validate(destination, version);
        return destination;
    }

    public static void ValidateTarget(string target, string dataDirectory)
    {
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        dataDirectory = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (dataDirectory.Equals(target, StringComparison.OrdinalIgnoreCase)
            || IsWithin(target, dataDirectory)
            || ManagedEntries.Any(e => IsWithin(dataDirectory, Path.Combine(target, e))))
            throw new IOException("配置目录与程序更新目录重叠，请将程序放在配置目录以外。");
        foreach (var entry in ManagedEntries.Append("")) RejectReparseAncestors(Path.Combine(target, entry));
    }
    public static bool IsWithin(string path, string root) => Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static void RejectReparseAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("更新路径不允许目录链接或重解析点。");
    }
    public static void CheckSpace(string path, long needed)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!);
        if (drive.AvailableFreeSpace < needed) throw new IOException("磁盘空间不足，当前版本未被替换。");
    }
}
