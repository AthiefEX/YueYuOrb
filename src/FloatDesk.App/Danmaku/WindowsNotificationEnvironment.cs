using System.Runtime.InteropServices;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Danmaku;

public readonly record struct NotificationDisplayContext(bool Fullscreen, nint Window, string? MonitorId);

public static class WindowsNotificationEnvironment
{
    internal static bool IsApplicationWindowClass(string name) => name is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "MultitaskingViewFrame");
    public static NotificationDisplayContext Sample()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetDesktopWindow() || hwnd == GetShellWindow() || IsIconic(hwnd)) return new(false, hwnd, null);
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return new(false, hwnd, null);
        var characters = new char[256]; var length = GetClassName(hwnd, characters, characters.Length);
        if (length <= 0 || !IsApplicationWindowClass(new string(characters, 0, length))) return new(false, hwnd, null);
        return new(DisplayTopology.IsLikelyFullscreen(hwnd), hwnd, DisplayTopology.FromWindow(hwnd)?.DeviceName);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, [Out] char[] name, int count);
}
