using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Cut.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private int registeredId;
    private int nextId = 100;
    public int LastError { get; private set; }
    public event Action? Pressed;
    public HotkeyService()
    {
        source = new HwndSource(new HwndSourceParameters("Cut.Hotkey") { ParentWindow = new IntPtr(-3) });
        source.AddHook(Hook);
    }
    public bool TryRegister(uint modifiers, uint key)
    {
        int id = nextId++;
        if (!NativeMethods.RegisterHotKey(source.Handle, id, modifiers | 0x4000, key)) { LastError = Marshal.GetLastWin32Error(); return false; }
        LastError = 0;
        if (registeredId != 0) NativeMethods.UnregisterHotKey(source.Handle, registeredId);
        registeredId = id;
        return true;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == registeredId) { handled = true; Pressed?.Invoke(); }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (registeredId != 0) NativeMethods.UnregisterHotKey(source.Handle, registeredId);
        source.RemoveHook(Hook);
        source.Dispose();
    }
}
