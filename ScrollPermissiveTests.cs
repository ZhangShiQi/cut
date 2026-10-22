using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Cut.Core;

namespace Cut;

internal static class ScrollPermissiveTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Feed(ScrollCaptureWindow window, ScrollFrame frame)
    { for (int i = 0; i < 3; i++) await window.AcceptFrameAsync(frame); }
    public static async Task<int> RunAsync()
    {
        Directory.CreateDirectory("artifacts");
        var log = new StringBuilder("Opt-in low-confidence stitching: synthetic pixels only.\n");
        ScrollCaptureWindow? window = null;
        try
        {
            var image = ScrollTests.Fixture();
            ScrollFrame Crop(int y) => ScrollFrame.From(new CroppedBitmap(image, new Int32Rect(0, y, 640, 500)));
            var first = Crop(0); var moved = Crop(120); byte[] pixels = (byte[])moved.Pixels.Clone();
            for (int y = 80; y < 240; y++) for (int x = 100; x < 350; x++) pixels[(y * 640 + x) * 4] = 0;
            var changed = new ScrollFrame(640, 500, pixels);
            foreach (var direction in Enum.GetValues<ScrollDirection>())
            {
                var a = direction == ScrollDirection.Down ? first : changed;
                var b = direction == ScrollDirection.Down ? changed : first;
                var matcher = new ScrollMatchingSession();
                Check(matcher.Match(a, b, direction).Kind == ScrollMatchKind.Uncertain, "Strict mode rejects changed overlap");
                var allowed = matcher.Match(a, b, direction, true);
                Check(allowed.Kind == ScrollMatchKind.Matched && allowed.Distance == 120 && allowed.Unreliable, "Opt-in uses actual estimated displacement: " + allowed);
                Check(matcher.Match(a, b, direction).Kind == ScrollMatchKind.Uncertain, "Turning off restores strict matching");
                var reverse = matcher.Match(b, a, direction, true);
                Check(reverse.Kind == ScrollMatchKind.Uncertain, "Reverse movement not forced");
            }
            var safe = new ScrollMatchingSession().Match(first, moved, ScrollDirection.Down, true);
            Check(safe.Kind == ScrollMatchKind.Matched && !safe.Unreliable, "Opt-in keeps reliable matches reliable");
            var blank = new ScrollFrame(640, 500, new byte[640 * 500 * 4]);
            Check(new ScrollMatchingSession().Match(blank, blank, ScrollDirection.Down, true).Kind == ScrollMatchKind.Same, "Blank unchanged frame is not appended");
            Check(new ScrollMatchingSession().Match(first, blank, ScrollDirection.Down, true).Kind == ScrollMatchKind.Uncertain, "No features cannot invent distance");
            Check(new ScrollMatchingSession().Match(first, Crop(1000), ScrollDirection.Down, true).Kind == ScrollMatchKind.Uncertain, "No overlap cannot invent distance");
            log.AppendLine("PASS strict/opt-in/off, both directions exact estimated distance, reliable flag, reverse/blank/no-feature/no-overlap guards");
            window = new(new(20, 20, 640, 500), new(0, 0, 2000, 1200))
            { AutomaticPolling = false, ValidateForTest = () => true, MatchRetryMilliseconds = 0 };
            Check(!window.AllowUnreliable, "Default off");
            window.Show(); await window.ToggleAsync(); await Feed(window, first); await Feed(window, changed);
            Check(!window.Running && window.Document?.Appends == 0, "Strict controller pauses");
            window.AllowUnreliable = true; await window.ToggleAsync(); await Feed(window, changed);
            Check(window.Running && window.Document!.UnreliableAppends == 1 && window.Document.Height == 620, "Opt-in resumes existing capture and records uncertain seam");
            window.Pause(); window.AllowUnreliable = false;
            Check(window.Document!.UnreliableAppends == 1, "Switching off retains existing uncertainty flag");
            ScrollTests.Preview(window, "artifacts/scroll-permissive-preview.png");
            await window.UndoAsync(); Check(window.Document.UnreliableAppends == 0, "Undo removes uncertainty count");
            window.AllowUnreliable = true; await window.ToggleAsync(); await Feed(window, changed); await window.CompleteAsync();
            var editor = new LongImageEditor(window.Result!); editor.SetUnreliableSeams(window.Document.UnreliableAppends);
            editor.Show(); ScrollTests.Preview(editor, "artifacts/scroll-permissive-editor.png"); editor.Close();
            window = new(new(20, 20, 640, 500), new(0, 0, 2000, 1200));
            Check(!window.AllowUnreliable, "New capture resets to strict mode"); window.Close();
            long clock = 0; int inputs = 0;
            window = new(new(20, 20, 640, 500), new(0, 0, 2000, 1200))
            {
                AutoScroll = true, AllowUnreliable = true, AutomaticPolling = false, ValidateForTest = () => true,
                SkipAutoGuardsForTest = true, CountdownMilliseconds = 0, Now = () => clock,
                Capture = _ => (inputs == 0 ? first : changed).Image(), SendWheel = _ => { inputs++; return true; }
            };
            window.Show(); await window.ToggleAsync();
            for (int i = 0; i < 6; i++) { clock += 180; await window.PollAsync(); }
            Check(window.Running && window.Document?.UnreliableAppends == 1 && inputs == 2, "Automatic mode continues after opted-in seam");
            window.Pause(); await window.CompleteAsync();
            log.AppendLine("PASS manual and auto controller, session-only default, resume, warning count, undo, completion and editor preview");
            log.AppendLine("ALL PERMISSIVE STITCHING CHECKS PASSED");
            File.WriteAllText("artifacts/scroll-permissive-test.txt", log.ToString()); return 0;
        }
        catch (Exception ex) { window?.Close(); Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/scroll-permissive-test.txt", log + ex.ToString()); return 1; }
    }
}
