using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FloatDesk.App.Views;

/// <summary>Positions and clips a native top-level VMConnect window without
/// changing the RDP ActiveX control's window hierarchy.</summary>
internal sealed class VmConnectSurface : FrameworkElement, IDisposable
{
    private readonly nint _console;
    private readonly uint _processId;
    private int _desktopWidth;
    private int _desktopHeight;
    private int _offsetX;
    private int _offsetY;
    private int _left;
    private int _top;
    private int _right;
    private int _bottom;
    private int _originalExStyle;
    private int _originalStyle;
    private int _originalRenderingPolicy;
    private bool _hasOriginalRenderingPolicy;
    private nint _originalOwner;
    private Window? _owner;
    private bool _attached;
    private bool _disposed;
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => _ready.Task;
    private (int X, int Y, int Width, int Height, int ViewWidth, int ViewHeight, int OffsetX, int OffsetY)? _last;

    internal VmConnectSurface(nint console, int desktopWidth, int desktopHeight)
    {
        _console = console;
        _ = GetWindowThreadProcessId(console, out _processId);
        _desktopWidth = desktopWidth;
        _desktopHeight = desktopHeight;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => Position();
        Focusable = false;
        _sync.Tick += (_, _) => Position();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!_disposed && !TryGetDesktopPane(_console, out _) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            if (_disposed) { _ready.TrySetCanceled(); return; }
            Attach();
            _ready.TrySetResult();
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
    }

    private void Attach()
    {
        if (_attached || _disposed) return;
        if (!TryGetDesktopPane(_console, out var desktop) ||
            !GetWindowRect(desktop, out var pane) || !GetWindowRect(_console, out var frame))
            throw new InvalidOperationException("未找到 VMConnect 桌面画面。");
        _left = Math.Max(0, pane.Left - frame.Left);
        _top = Math.Max(0, pane.Top - frame.Top);
        _right = Math.Max(0, frame.Right - pane.Right);
        _bottom = Math.Max(0, frame.Bottom - pane.Bottom);
        _owner = Window.GetWindow(this) ?? throw new InvalidOperationException("控制台窗口尚未就绪。");
        _originalExStyle = GetWindowLong(_console, -20);
        _originalStyle = GetWindowLong(_console, -16);
        _hasOriginalRenderingPolicy = DwmGetWindowAttribute(_console, 2, out _originalRenderingPolicy, 4) == 0;
        _originalOwner = GetWindowLongPtr(_console, -8);
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(_console, -8, new WindowInteropHelper(_owner).Handle) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Keep the RDP HWND hierarchy intact, but let the branded shell own all
        // window chrome. Region clipping alone leaves DWM's native caption alive.
        _ = SetWindowLong(_console, -20, (_originalExStyle | 0x80) & ~0x40000);
        var disabled = 1; // DWMNCRP_DISABLED
        _ = DwmSetWindowAttribute(_console, 2, ref disabled, 4);
        if (!RemoveNativeChrome()) throw new Win32Exception(Marshal.GetLastWin32Error());
        _owner.LocationChanged += OnOwnerMoved;
        _owner.SizeChanged += OnOwnerSized;
        _owner.StateChanged += OnOwnerMoved;
        _owner.Activated += OnOwnerMoved;
        _attached = true;
        Position();
        _sync.Start();
    }

    internal void SetDesktopViewport(int width, int height, int x, int y)
    {
        _desktopWidth = width; _desktopHeight = height; _offsetX = x; _offsetY = y;
        Position();
    }

    private void OnOwnerMoved(object? sender, EventArgs args) => Position();
    private void OnOwnerSized(object sender, SizeChangedEventArgs args) => Position();

    private void Position()
    {
        if (!_attached || _disposed || _owner is null || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_owner.WindowState == WindowState.Minimized) return;
        if (!OwnsWindow()) return;
        if (!RemoveNativeChrome()) return;
        // VMConnect can resize itself and reset its region when an enhanced
        // session finishes connecting. Read live pane insets after that layout.
        if (TryGetDesktopPane(_console, out var desktop) && GetWindowRect(desktop, out var pane) &&
            GetWindowRect(_console, out var frame))
        {
            _left = Math.Max(0, pane.Left - frame.Left);
            _top = Math.Max(0, pane.Top - frame.Top);
            _right = Math.Max(0, frame.Right - pane.Right);
            _bottom = Math.Max(0, frame.Bottom - pane.Bottom);
        }
        var origin = PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(this);
        var viewWidth = Math.Min(_desktopWidth, Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)));
        var viewHeight = Math.Min(_desktopHeight, Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
        var x = (int)Math.Round(origin.X) - _left - _offsetX;
        var y = (int)Math.Round(origin.Y) - _top - _offsetY;
        var width = _desktopWidth + _left + _right;
        var height = _desktopHeight + _top + _bottom;
        var next = (x, y, width, height, viewWidth, viewHeight, _offsetX, _offsetY);
        if (_last == next && GeometryMatches(x, y, width, height, viewWidth, viewHeight)) return;
        var region = CreateRectRgn(_left + _offsetX, _top + _offsetY,
            _left + _offsetX + viewWidth, _top + _offsetY + viewHeight);
        if (region == 0) return;
        if (SetWindowRgn(_console, region, true) == 0)
        {
            DeleteObject(region);
            return;
        }
        // SetWindowRgn owns the HRGN after success.
        SetWindowPos(_console, 0, x, y, width, height, 0x0010);
        _last = next;
    }

    private bool GeometryMatches(int x, int y, int width, int height, int viewWidth, int viewHeight)
    {
        if (!GetWindowRect(_console, out var frame) || frame.Left != x || frame.Top != y ||
            frame.Right - frame.Left != width || frame.Bottom - frame.Top != height) return false;
        var region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return false;
        try
        {
            return GetWindowRgn(_console, region) != 0 && GetRgnBox(region, out var bounds) != 0 &&
                bounds.Left == _left + _offsetX && bounds.Top == _top + _offsetY &&
                bounds.Right == _left + _offsetX + viewWidth && bounds.Bottom == _top + _offsetY + viewHeight;
        }
        finally { DeleteObject(region); }
    }

    private bool OwnsWindow()
    {
        _ = GetWindowThreadProcessId(_console, out var actual);
        return _processId != 0 && actual == _processId;
    }

    private bool RemoveNativeChrome()
    {
        var style = GetWindowLong(_console, -16);
        const int chrome = 0x00CF0000; // Caption, thick frame, system menu and caption buttons.
        if ((style & chrome) == 0) return true;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(_console, -16, style & ~chrome) == 0 && Marshal.GetLastWin32Error() != 0)
            return false;
        _ = SetWindowPos(_console, 0, 0, 0, 0, 0, 0x0037); // FRAMECHANGED, NOMOVE/NOSIZE/NOZORDER/NOACTIVATE
        _last = null;
        return true;
    }
    internal void Refresh() { if (OwnsWindow()) RedrawWindow(_console, 0, 0, 0x0185); }
    internal void CloseConsole() { if (OwnsWindow()) PostMessage(_console, 0x10, 0, 0); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sync.Stop();
        _ready.TrySetCanceled();
        if (_owner is not null)
        {
            _owner.LocationChanged -= OnOwnerMoved;
            _owner.SizeChanged -= OnOwnerSized;
            _owner.StateChanged -= OnOwnerMoved;
            _owner.Activated -= OnOwnerMoved;
        }
        if (!OwnsWindow()) return;
        ShowWindow(_console, 0);
        if (_attached)
        {
            _ = SetWindowLong(_console, -16, _originalStyle);
            if (_hasOriginalRenderingPolicy) _ = DwmSetWindowAttribute(_console, 2, ref _originalRenderingPolicy, 4);
            _ = SetWindowLongPtr(_console, -8, _originalOwner);
            _ = SetWindowLong(_console, -20, _originalExStyle);
        }
        CloseConsole();
    }

    internal static nint FindWindow(int processId)
    {
        nint result = 0;
        long largest = 0;
        EnumWindows((window, parameter) =>
        {
            _ = GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || GetWindow(window, 4) != 0) return true;
            if (!IsConsoleFrame(window)) return true;
            if (!GetWindowRect(window, out var rectangle) || rectangle.Right - rectangle.Left < 100 ||
                rectangle.Bottom - rectangle.Top < 100) return true;
            var area = (long)(rectangle.Right - rectangle.Left) * (rectangle.Bottom - rectangle.Top);
            if (area > largest) { result = window; largest = area; }
            return true;
        }, 0);
        return result;
    }

    internal static bool IsConsoleFrameClass(string name) => name.StartsWith("WindowsForms10.Window.", StringComparison.Ordinal);
    internal static bool IsConsoleFrame(nint window)
    {
        var name = new char[256];
        var length = GetClassName(window, name, name.Length);
        return length > 0 && IsConsoleFrameClass(new string(name, 0, length));
    }

    internal static bool TryGetDesktopPane(nint window, out nint desktop)
    {
        desktop = 0;
        if (!IsConsoleFrame(window)) return false;
        if (GetProp(window, "YueYu.Rdp.Client") != 0 && GetProp(window, "YueYu.Rdp.Ready") == 0) return false;
        long largest = 0;
        var count = 0;
        for (var child = GetWindow(window, 5); child != 0; child = GetWindow(child, 2))
        {
            count++;
            if (!GetWindowRect(child, out var bounds)) continue;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width < 320 || height < 200) continue;
            var area = (long)width * height;
            if (area > largest) { largest = area; desktop = child; }
        }
        return count >= 3 && desktop != 0;
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint window, nint rectangle, nint region, uint flags);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] internal static extern bool IsNativeVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, [Out] char[] name, int length);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out NativeRect bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
}
