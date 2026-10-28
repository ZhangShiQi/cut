using System.Diagnostics;
using Cut.UpdateEngine;

internal static partial class Program
{
    private static string _root = "";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--launcher-worker") return LauncherWorker(args);
        if (args.FirstOrDefault() == "--health-probe") return HealthProbe(args);
        if (args.FirstOrDefault() == "--portable") return PortableSmoke(args[1], args[2]);
        _root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Run("release API, download, authentication and package boundaries", ClientAndPackageTests);
            Run("successful upgrade and startup crash/timeout/readiness failure", LauncherSuccessAndFailures);
            Run("12-stage forced launcher termination and durable recovery", LauncherForcedInterruptionMatrix);
            Run("repeated interruption during configuration recovery", LauncherInterruptedRecovery);
            Run("corrupt snapshots and state generations", LauncherCorruptionAndSnapshots);
            Run("post-commit rollback preserves new user configuration", LauncherLateFailurePreservesNewEdits);
            Run("incompatible schema/protocol/components and Windows file locks", LauncherInvalidCandidates);
            File.WriteAllText(Path.Combine(_root, "result.txt"), "ALL UPDATE CHECKS PASSED\n");
            return 0;
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(_root, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run(string name, Action action) { var timer = Stopwatch.StartNew(); action(); Console.WriteLine($"PASS {name} ({timer.Elapsed.TotalSeconds:0.0}s)"); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

namespace Cut.Updates
{
    // Production token storage is tested in the WPF UI suite; HTTP tests inject credentials.
    public static class UpdateCredentials
    {
        public static Task<string?> ReadAsync(bool interactive, CancellationToken token) => Task.FromResult<string?>(null);
    }
}
