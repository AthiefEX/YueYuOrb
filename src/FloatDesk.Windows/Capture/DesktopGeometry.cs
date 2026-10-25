using FloatDesk.Core.Geometry;
using FloatDesk.Windows.Native;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Windows.Capture;

public static class DesktopGeometry
{
    public static PixelPoint GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out var point))
            throw new InvalidOperationException("Unable to read the system cursor position.");
        return new PixelPoint(point.X, point.Y);
    }

    public static PixelRect GetVirtualDesktopBounds()
    {
        var monitors = DisplayTopology.GetMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No display monitors were found.");
        var left = monitors.Min(monitor => monitor.Bounds.Left);
        var top = monitors.Min(monitor => monitor.Bounds.Top);
        var right = monitors.Max(monitor => monitor.Bounds.Right);
        var bottom = monitors.Max(monitor => monitor.Bounds.Bottom);
        return new PixelRect(left, top, checked(right - left), checked(bottom - top));
    }

    public static PixelRect GetVisibleWindowBounds(nint hwnd)
    {
        if (hwnd == 0 || !NativeMethods.IsWindow(hwnd))
            throw new InvalidOperationException("The target window is no longer available.");
        var size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Rect>();
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DwmExtendedFrameBounds,
                out var bounds,
                size) != 0 &&
            !NativeMethods.GetWindowRect(hwnd, out bounds))
        {
            throw new InvalidOperationException("Unable to read the target window bounds.");
        }
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
            throw new InvalidOperationException("The target window has no visible capture area.");
        return new PixelRect(
            bounds.Left,
            bounds.Top,
            checked(bounds.Right - bounds.Left),
            checked(bounds.Bottom - bounds.Top));
    }

    public static void SetWindowPixelBounds(nint hwnd, PixelRect bounds)
    {
        if (!NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTopmost,
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow))
        {
            throw new InvalidOperationException("Unable to position the overlay window.");
        }
    }

    public static double GetWindowDpiScale(nint hwnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    public static void FlushComposition()
    {
        _ = NativeMethods.DwmFlush();
    }
}
