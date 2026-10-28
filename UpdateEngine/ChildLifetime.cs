using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cut.UpdateEngine;

// Until startup is committed, Windows owns the child lifetime. Even TerminateProcess on
// the launcher closes this handle and terminates an unconfirmed child before recovery.
internal sealed class ChildLifetime : IDisposable
{
    private readonly nint _job;
    public ChildLifetime(Process child)
    {
        _job = CreateJobObjectW(0, null);
        try
        {
            if (_job == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetKillOnClose(true);
            if (!AssignProcessToJobObject(_job, child.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { if (_job != 0) CloseHandle(_job); throw; }
    }
    public void Release() => SetKillOnClose(false);
    private void SetKillOnClose(bool enabled)
    {
        var info = new ExtendedLimits { BasicLimitInformation = new BasicLimits { LimitFlags = enabled ? 0x2000u : 0u } };
        if (!SetInformationJobObject(_job, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public void Dispose() => CloseHandle(_job);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits BasicLimitInformation; public IoCounters IoInfo; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(nint job, int type, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
