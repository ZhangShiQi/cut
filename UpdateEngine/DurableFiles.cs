using System.Security.Cryptography;
using System.Text.Json;
using Cut.UpdateEngine;

namespace Cut.UpdateEngine;

public static class DurableFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    // Temp file and replacement are on one volume. Flush file contents before publishing the name.
    // Both generations are kept: a torn/corrupt primary can be read from the previous generation.
    public static void Write<T>(string path, T value) => WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json));
    public static void WriteBytes(string path, byte[] bytes, bool backup = true)
    {
        UpdatePackage.RejectReparseAncestors(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        RetryIO(() =>
        {
            if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null, true);
            else File.Move(temporary, path);
        });
    }
    public static void RetryIO(Action action)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { action(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && timer.Elapsed < TimeSpan.FromSeconds(5))
            { Thread.Sleep(100); }
        }
    }
    public static T Read<T>(string path)
    {
        Exception? error = null;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                UpdatePackage.RejectReparseAncestors(candidate);
                return JsonSerializer.Deserialize<T>(File.ReadAllText(candidate), Json) ?? throw new InvalidDataException("Empty state: " + candidate);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { error = ex; }
        }
        throw new IOException("无法读取更新状态，已保留原文件：" + path, error);
    }
    public static bool Exists(string path) => File.Exists(path) || File.Exists(path + ".bak");
    public static void Delete(string path)
    {
        // Delete the older generation first so a crash cannot resurrect a completed transaction.
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        if (File.Exists(path)) File.Delete(path);
    }
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static string Child(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new InvalidDataException("无效的更新相对路径。");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!UpdatePackage.IsWithin(result, root)) throw new InvalidDataException("更新路径越界。");
        UpdatePackage.RejectReparseAncestors(result);
        return result;
    }
}
