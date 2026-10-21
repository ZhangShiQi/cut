using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Cut.Core;

namespace Cut.Services;

public sealed record WindowCandidate(IntPtr Handle, PixelRect Bounds);

public static class WindowDetectionService
{
    // Record once, before the capture overlays exist. Enumerating on every mouse
    // move would disagree with the frozen desktop when other windows move.
    public static IReadOnlyList<WindowCandidate> Capture(uint? excludedProcessId = null)
    {
        uint excluded = excludedProcessId ?? (uint)Environment.ProcessId;
        var windows = new List<WindowCandidate>();
        NativeMethods.WindowCallback callback = (window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == excluded) return true;
            if (NativeMethods.GetWindowCloaked(window, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (IsTransparentOverlay(window)) return true;
            var name = new StringBuilder(256);
            NativeMethods.GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "tooltips_class32") return true;
            var bounds = GetFrameBounds(window);
            if (!bounds.IsEmpty) windows.Add(new(window, bounds));
            return true;
        };
        // EnumWindows visits top-level windows in front-to-back Z order.
        if (!NativeMethods.EnumWindows(callback, IntPtr.Zero)) throw new Win32Exception();
        return windows;
    }

    private static bool IsTransparentOverlay(IntPtr window)
    {
        const int extendedStyleIndex = -20;
        const int layeredStyle = 0x00080000;
        const int transparentStyle = 0x00000020;
        const uint alphaFlag = 0x00000002;
        int style = NativeMethods.GetWindowLong(window, extendedStyleIndex);
        if ((style & layeredStyle) == 0) return false;
        // GPU/recording overlays can be WS_VISIBLE, uncloaked and monitor-sized.
        // WS_EX_LAYERED + WS_EX_TRANSPARENT passes mouse input to windows below.
        if ((style & transparentStyle) != 0) return true;
        // A zero alpha value is meaningful only when LWA_ALPHA is actually set.
        // Preserve ordinary interactive translucent windows and per-pixel layers
        // whose global alpha cannot be queried.
        return NativeMethods.GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags)
            && (flags & alphaFlag) != 0 && alpha == 0;
    }

    internal static PixelRect GetFrameBounds(IntPtr window)
    {
        // DWM visible bounds exclude invisible resize borders, in physical pixels.
        if (NativeMethods.GetWindowFrame(window, 9, out var rect, 16) != 0 && !NativeMethods.GetWindowRect(window, out rect)) return default;
        return new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    public static PixelRect FindAt(PixelPoint point, IReadOnlyList<WindowCandidate> windows, DesktopSnapshot desktop)
    {
        bool onMonitor = false;
        foreach (var monitor in desktop.Monitors) if (monitor.Bounds.Contains(point)) { onMonitor = true; break; }
        if (!onMonitor) return default;
        foreach (var window in windows)
        {
            var visible = window.Bounds.Intersect(desktop.Bounds);
            if (visible.Contains(point)) return visible;
        }
        foreach (var monitor in desktop.Monitors) if (monitor.Bounds.Contains(point)) return monitor.Bounds;
        return default;
    }
}
