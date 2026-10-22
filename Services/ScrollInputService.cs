using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cut.Services;

// Hooks are installed only while automatic capture is active. No input is stored.
internal sealed class ScrollInputService : IDisposable
{
    private static readonly UIntPtr Tag = new(0x43555453u);
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    private readonly HookProc mouseCallback, keyboardCallback;
    private IntPtr mouseHook, keyboardHook;
    private readonly Action interrupted;
    public bool Interrupted { get; private set; }

    [StructLayout(LayoutKind.Sequential)] private struct MouseData
    { public NativeMethods.Point Point; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    // INPUT is 40 bytes on x64, with its union aligned to 8 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)] private struct Input
    { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);

    internal static bool HasHeldInput()
    {
        // A modifier or drag started during the countdown must not turn wheel input
        // into zoom, horizontal scroll, or a drag operation.
        foreach (int key in new[] { 1, 2, 4, 5, 6, 16, 17, 18, 91, 92 })
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    public ScrollInputService(Action interrupted)
    {
        this.interrupted = interrupted;
        mouseCallback = Mouse; keyboardCallback = Keyboard;
        mouseHook = SetWindowsHookExW(14, mouseCallback, GetModuleHandle(null), 0);
        keyboardHook = SetWindowsHookExW(13, keyboardCallback, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero)
        { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error, "无法监听暂停操作"); }
    }
    private void Interrupt()
    {
        if (Interrupted) return;
        Interrupted = true; interrupted();
    }
    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        int kind = message.ToInt32();
        if (code >= 0 && kind is 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E)
        {
            var value = Marshal.PtrToStructure<MouseData>(data);
            if (value.Extra != Tag) Interrupt();
        }
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is 0x0100 or 0x0104) Interrupt();
        return CallNextHookEx(IntPtr.Zero, code, message, data);
    }
    public static bool Wheel(int delta)
    {
        var input = new Input { Type = 0, Mouse = new MouseInput { Data = unchecked((uint)delta), Flags = 0x0800, Extra = Tag } };
        return SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 1;
    }
    public void Dispose()
    {
        if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
    }
}
