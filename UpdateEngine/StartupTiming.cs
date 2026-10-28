using System.Diagnostics;
using System.Text.Json;

namespace Cut.UpdateEngine;

// Opt-in local diagnostics. Never records note text, paths, credentials, or network requests.
public static class StartupTiming
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("CUT_STARTUP_TRACE_DIR");
    public static void Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(Path.Combine(DirectoryPath, process.Id + ".jsonl"), JsonSerializer.Serialize(new
            { pid = process.Id, phase, time = DateTimeOffset.UtcNow, processElapsedMs = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds }) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception) { }
    }
}
