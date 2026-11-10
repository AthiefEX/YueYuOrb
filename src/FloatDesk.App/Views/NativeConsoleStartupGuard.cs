using System;
using System.Runtime.InteropServices;

namespace FloatDesk.App.Views;

/// <summary>VMConnect calls Show itself even with SW_HIDE. Conceal its native
/// frame during creation until its owned viewport geometry is ready.</summary>
internal sealed class NativeConsoleStartupGuard : IDisposable
{
    private readonly EventCallback _callback;
    private nint _hook;

    internal NativeConsoleStartupGuard(int processId)
    {
        _callback = OnEvent;
        _hook = SetWinEventHook(0x8000, 0x8002, 0, _callback, (uint)processId, 0, 0);
        if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var existing = VmConnectSurface.FindWindow(processId);
        if (existing != 0) Conceal(existing);
    }

    internal static bool IsConcealed(nint window) => !IsWindowVisible(window) ||
        (DwmGetWindowAttribute(window, 14, out var cloaked, 4) == 0 && cloaked != 0) ||
        (GetWindowRect(window, out var rectangle) && (rectangle.Right <= 0 || rectangle.Bottom <= 0));
    private void OnEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_hook == 0 || objectId != 0 || childId != 0 || window == 0 || GetParent(window) != 0 ||
            !VmConnectSurface.IsConsoleFrame(window)) return;
        Conceal(window);
    }

    private static void Conceal(nint window)
    {
        // Never synchronously send style/move messages from a cross-process
        // creation event: VMConnect may still be initializing its UI thread.
        var cloak = 1;
        _ = DwmSetWindowAttribute(window, 13, ref cloak, 4);
        SetWindowPos(window, 0, -32000, -32000, 0, 0, 0x4015);
    }

    internal static void ConcealWindow(nint window) => Conceal(window);
    internal void Reveal(nint window)
    {
        Dispose();
        // Geometry and clipping are already installed. Reveal without moving or
        // reparenting the native RDP window.
        var cloak = 0;
        _ = DwmSetWindowAttribute(window, 13, ref cloak, 4);
        _ = ShowWindow(window, 4);
    }

    public void Dispose()
    {
        if (_hook == 0) return;
        UnhookWinEvent(_hook);
        _hook = 0;
    }

    private delegate void EventCallback(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, EventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
