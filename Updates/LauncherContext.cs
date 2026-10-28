using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Cut.UpdateEngine;

namespace Cut.Updates;

public static class LauncherContext
{
    public static string? Root { get; private set; }
    public static string? VersionRoot { get; private set; }
    public static string? Argument(string[] args, string key)
    {
        var i = Array.IndexOf(args, key);
        if (i < 0) return null;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("缺少参数：" + key);
        return args[i + 1];
    }
    public static bool ConfigureOrRedirect(string[] args)
    {
        var root = Argument(args, "--launcher-root");
        var slot = Argument(args, "--version-root");
        if (root is not null && slot is not null)
        {
            root = Path.GetFullPath(root); slot = Path.GetFullPath(slot);
            var executable = DurableFiles.Child(slot, VersionLayout.Runtime);
            if (!string.Equals(Environment.ProcessPath, executable, StringComparison.OrdinalIgnoreCase)
                || !LauncherStore.IsManaged(root) || !(slot.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || UpdatePackage.IsWithin(slot, Path.Combine(root, "versions")))) throw new IOException("启动器与运行程序路径不匹配。");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(slot, "release-manifest.json")));
            if (manifest.RootElement.GetProperty("version").GetString() != UpdateRelease.CurrentVersion
                || manifest.RootElement.GetProperty("dataSchemaVersion").GetInt32() != VersionLayout.DataSchema)
                throw new IOException("运行程序版本或数据格式与更新清单不匹配。");
            Root = root; VersionRoot = slot;
            AppContext.SetData("Cut.ToolsDirectory", slot);
            return false;
        }
        if (root is not null || slot is not null) throw new ArgumentException("启动器参数不完整。");
        // Starting the inner executable directly must still go through startup recovery and health confirmation.
        var possibleSlot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        if (!File.Exists(Path.Combine(possibleSlot, "release-manifest.json"))) return false;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(possibleSlot, "release-manifest.json")));
        if (!doc.RootElement.TryGetProperty("formatVersion", out var format) || format.GetInt32() != 2) return false;
        var possibleRoot = Directory.GetParent(possibleSlot)?.Name == "versions" ? Directory.GetParent(possibleSlot)!.Parent!.FullName : possibleSlot;
        var info = new ProcessStartInfo(Path.Combine(possibleRoot, "Cut.exe")) { UseShellExecute = false, WorkingDirectory = possibleRoot };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        Process.Start(info)?.Dispose();
        return true;
    }
}
