using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatDesk.App.Views;

public sealed class NightDimWindow : Window
{
    public NightDimWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Black;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        IsHitTestVisible = false;
        Focusable = false;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _ = SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x20 | 0x08000000 | 0x80);
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
        };
    }

    public void SetPhysicalBounds(System.Drawing.Rectangle rect)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(handle, new nint(-1), rect.Left, rect.Top, rect.Width, rect.Height, 0x10);
    }
    private static nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if (message == 0x84) { handled = true; return new nint(-1); }
        if (message == 0x21) { handled = true; return new nint(3); }
        return 0;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
