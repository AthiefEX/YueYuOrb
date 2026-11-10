using System.Runtime.InteropServices;

namespace FloatDesk.Windows.DesktopSessions;

public interface IChildSessionNative
{
    bool SupportsChildSessions { get; }

    bool TryGetEnabled(out bool enabled);

    bool TrySetEnabled(bool enabled);

    bool TryGetChildSessionId(out uint sessionId);

    bool TryLogoffSession(uint sessionId);
}

public sealed class ChildSessionNative : IChildSessionNative
{
    private static readonly Lazy<bool> ChildSessionsSupported =
        new(ProbeChildSessionExports, isThreadSafe: true);

    public bool SupportsChildSessions => ChildSessionsSupported.Value;

    public bool TryGetEnabled(out bool enabled)
    {
        enabled = false;
        if (!SupportsChildSessions)
            return false;

        try
        {
            return WtsIsChildSessionsEnabled(out enabled);
        }
        catch (EntryPointNotFoundException)
        {
            enabled = false;
            return false;
        }
        catch (DllNotFoundException)
        {
            enabled = false;
            return false;
        }
    }

    public bool TrySetEnabled(bool enabled)
    {
        if (!SupportsChildSessions)
            return false;

        try
        {
            return WtsEnableChildSessions(enabled);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public bool TryGetChildSessionId(out uint sessionId)
    {
        sessionId = uint.MaxValue;
        if (!SupportsChildSessions)
            return false;

        try
        {
            if (!WtsGetChildSessionId(out sessionId) || sessionId is 0 or uint.MaxValue)
                return false;
            if (WtsQuerySessionInformation(IntPtr.Zero, sessionId, 8, out var buffer, out _))
            {
                WtsFreeMemory(buffer);
                return true;
            }
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 7022)
            {
                sessionId = uint.MaxValue;
                return false;
            }
            throw new System.ComponentModel.Win32Exception(error, "Child session existence query failed.");
        }
        catch (EntryPointNotFoundException)
        {
            sessionId = uint.MaxValue;
            return false;
        }
        catch (DllNotFoundException)
        {
            sessionId = uint.MaxValue;
            return false;
        }
    }

    public bool TryLogoffSession(uint sessionId)
    {
        if (!SupportsChildSessions || sessionId == 0)
            return false;

        try
        {
            return WtsLogoffSession(IntPtr.Zero, sessionId, wait: true);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static bool ProbeChildSessionExports()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        if (!NativeLibrary.TryLoad("wtsapi32.dll", out var module))
            return false;

        try
        {
            return NativeLibrary.TryGetExport(
                       module,
                       "WTSEnableChildSessions",
                       out _) &&
                   NativeLibrary.TryGetExport(
                       module,
                       "WTSIsChildSessionsEnabled",
                       out _) &&
                   NativeLibrary.TryGetExport(
                       module,
                       "WTSGetChildSessionId",
                       out _);
        }
        finally
        {
            NativeLibrary.Free(module);
        }
    }

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSIsChildSessionsEnabled",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsIsChildSessionsEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSEnableChildSessions",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsEnableChildSessions(
        [MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSGetChildSessionId",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static extern void WtsFreeMemory(IntPtr buffer);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSLogoffSession",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsLogoffSession(
        IntPtr server,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);
}
