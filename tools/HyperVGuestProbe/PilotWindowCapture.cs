using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class PilotWindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    private delegate bool WindowCallback(nint window, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    public static bool Capture(Process process, string path)
    {
        using var current = Process.GetProcessById(process.Id);
        if (current.HasExited) return false;
        Rect bounds = default;
        long largest = 0;
        EnumWindows((window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var owner) == 0) return true;
            if (owner != (uint)process.Id || !IsWindowVisible(window) || !GetWindowRect(window, out var candidate)) return true;
            var area = (long)(candidate.Right - candidate.Left) * (candidate.Bottom - candidate.Top);
            if (area > largest) { largest = area; bounds = candidate; }
            return true;
        }, 0);
        var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
        if (width is < 1 or > 4096 || height is < 1 or > 2160) return false;
        using var bitmap = new Bitmap(width, height); using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        bitmap.Save(path);
        return true;
    }
}
