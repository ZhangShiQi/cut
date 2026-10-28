using System;
using System.IO;
using Microsoft.Win32;

namespace Cut.Services;

internal interface IStartupRegistration
{
    bool IsEnabled { get; }
    bool IsCurrentLocation { get; }
    void SetEnabled(bool enabled);
}

// Current-user logon entry. Reading the setting never creates a key or enables it.
internal sealed class StartupRegistration : IStartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Cut.Screenshot";
    private readonly RegistryKey root;
    private readonly string keyPath;
    private readonly Func<string?> executable;
    public StartupRegistration() : this(Registry.CurrentUser, RunKey, () => Cut.Updates.LauncherContext.Root is string root ? Path.Combine(root, "Cut.exe") : Environment.ProcessPath) { }
    internal StartupRegistration(RegistryKey root, string keyPath, Func<string?> executable)
    { this.root = root; this.keyPath = keyPath; this.executable = executable; }

    private string? RegisteredCommand
    {
        get
        {
            using var key = root.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
    }
    public bool IsEnabled => !string.IsNullOrWhiteSpace(RegisteredCommand);
    public bool IsCurrentLocation => string.Equals(RegisteredCommand, Command(executable()), StringComparison.OrdinalIgnoreCase);
    internal static string Command(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('"') ||
            !string.Equals(Path.GetFileName(path), "Cut.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("请从完整解压的发布目录运行 Cut.exe，再开启自动启动。");
        string command = "\"" + Path.GetFullPath(path) + "\" --autostart";
        if (command.Length > 260) throw new InvalidOperationException("程序所在路径过长，请移到较短的路径后开启自动启动。");
        return command;
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = Command(executable());
            using var key = root.CreateSubKey(keyPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = root.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
    public bool MigrateLegacyEntry()
    {
        // Only migrate Cut's existing enabled entry from a version predating the launcher.
        // Never enable startup, replace an unrelated command, or move another managed install.
        var command = RegisteredCommand;
        if (command is null || IsCurrentLocation) return false;
        var match = System.Text.RegularExpressions.Regex.Match(command, "^\"([^\"]+)\" --autostart$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var previous = match.Groups[1].Value;
        if (!File.Exists(previous) || !Path.GetFileName(previous).Equals("Cut.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(previous);
        if (info.ProductName != "Cut" || info.FileMajorPart != 0 || info.FileMinorPart > 13) return false;
        SetEnabled(true); return true;
    }
}
