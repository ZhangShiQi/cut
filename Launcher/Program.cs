using System.Runtime.InteropServices;
using Cut.UpdateEngine;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        try
        {
            var data = UpdatePaths.Configure(args);
            if (args.Contains("--finish-update")) LauncherEngine.WaitForQueuedParent(root);
            return new LauncherEngine(root, new LauncherOptions { InstanceKey = UpdatePaths.TestKey }).Run(data, args.Contains("--autostart"));
        }
        catch (Exception ex)
        {
            try { DurableFiles.Write(Path.Combine(root, ".cut-update", "launcher-error.json"), new { error = ex.ToString(), time = DateTimeOffset.UtcNow }); }
            catch (Exception log) when (log is IOException or UnauthorizedAccessException) { }
            if (UpdatePaths.TestKey is null) MessageBoxW(0, "Cut 启动未完成。已保留配置、旧版本和恢复记录。\n\n" + ex.Message, "Cut", 0x10);
            return 1;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint window, string text, string title, uint type);
}
