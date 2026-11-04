using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FloatDesk.Windows.Diagnostics;

internal static class ResourceUsageNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhOpenQueryW(string? source, nuint userData, out PdhQueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhAddEnglishCounterW(PdhQueryHandle query, string path, nuint userData, out nint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCollectQueryData(PdhQueryHandle query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCloseQuery(nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format,
        ref uint bytes, out uint count, nint buffer);

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct CounterValue
    {
        [FieldOffset(0)] internal uint Status;
        [FieldOffset(8)] internal double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CounterItem
    {
        internal nint Name;
        internal CounterValue Value;
    }
}

internal sealed class PdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public PdhQueryHandle() : base(true) { }
    protected override bool ReleaseHandle() => ResourceUsageNative.PdhCloseQuery(handle) == 0;
}
