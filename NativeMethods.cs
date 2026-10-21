using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Cut.Core;

namespace Cut;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    public static PixelPoint CursorPosition()
    {
        if (!GetCursorPos(out var p)) throw new Win32Exception();
        return new(p.X, p.Y);
    }
    public static List<PixelRect> Monitors()
    {
        var result = new List<PixelRect>();
        MonitorCallback callback = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            result.Add(new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            return true;
        };
        if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero)) throw new Win32Exception();
        return result;
    }
}
