using System.Runtime.InteropServices;
using FloatDesk.Core.Geometry;
using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Windows;

public sealed record DisplayMonitor(
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    bool IsPrimary);

public static class DisplayTopology
{
    private const uint MonitorDefaultToNearest = 2;

    public static IReadOnlyList<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        NativeMethods.MonitorEnumProc callback = (
            nint monitor,
            nint deviceContext,
            nint monitorRect,
            nint data) =>
        {
            var info = new NativeMethods.MonitorInfo
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
                Device = string.Empty
            };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new DisplayMonitor(
                    info.Device,
                    ToPixelRect(info.Monitor),
                    ToPixelRect(info.WorkArea),
                    (info.Flags & 1) != 0));
            }

            return true;
        };
        NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        return monitors;
    }

    public static DisplayMonitor? FromPoint(PixelPoint point)
    {
        var monitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.Point(point.X, point.Y),
            MonitorDefaultToNearest);
        return FromHandle(monitor);
    }

    public static DisplayMonitor? FromWindow(nint hwnd)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        return FromHandle(monitor);
    }

    public static bool IsLikelyFullscreen(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;
        var monitor = FromWindow(hwnd);
        if (monitor is null)
            return false;
        const int tolerance = 2;
        return Math.Abs(rect.Left - monitor.Bounds.Left) <= tolerance &&
               Math.Abs(rect.Top - monitor.Bounds.Top) <= tolerance &&
               Math.Abs(rect.Right - monitor.Bounds.Right) <= tolerance &&
               Math.Abs(rect.Bottom - monitor.Bounds.Bottom) <= tolerance;
    }

    private static DisplayMonitor? FromHandle(nint handle)
    {
        if (handle == 0)
            return null;
        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
            Device = string.Empty
        };
        return NativeMethods.GetMonitorInfo(handle, ref info)
            ? new DisplayMonitor(
                info.Device,
                ToPixelRect(info.Monitor),
                ToPixelRect(info.WorkArea),
                (info.Flags & 1) != 0)
            : null;
    }

    private static PixelRect ToPixelRect(NativeMethods.Rect value) =>
        new(
            value.Left,
            value.Top,
            checked(value.Right - value.Left),
            checked(value.Bottom - value.Top));
}
