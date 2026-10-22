using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

namespace Cut.Services;

internal static class ScrollBoundaryService
{
    private static int busy;
    // A broken accessibility provider must not block the capture UI or spawn unlimited workers.
    public static async Task<bool> AtBoundaryAsync(IntPtr target, Point point, bool up)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        var query = Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(target);
                var element = AutomationElement.FromPoint(point);
                ScrollPattern? scroll = null;
                for (int depth = 0; element is not null && depth < 40; depth++)
                {
                    if (scroll is null && element.TryGetCurrentPattern(ScrollPattern.Pattern, out object pattern) &&
                        pattern is ScrollPattern candidate && candidate.Current.VerticallyScrollable) scroll = candidate;
                    if (element.Equals(root))
                    {
                        if (scroll is null) return false;
                        double percent = scroll.Current.VerticalScrollPercent;
                        return up ? percent == 0 : percent == 100;
                    }
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
                return false;
            }
            catch { return false; }
            finally { Volatile.Write(ref busy, 0); }
        });
        return await Task.WhenAny(query, Task.Delay(500)) == query && await query;
    }
}
