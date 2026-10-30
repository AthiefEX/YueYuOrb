using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;

namespace FloatDesk.App.Views;

internal static class PopupWindowInteraction
{
    internal static void EnableDragging(Window window, FrameworkElement header, Action? dragStarted = null)
    {
        header.Cursor = Cursors.SizeAll;
        header.MouseLeftButtonDown += (_, e) =>
        {
            for (var source = e.OriginalSource as DependencyObject; source is not null && source != header;)
            {
                if (source is ButtonBase) return;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { dragStarted?.Invoke(); window.DragMove(); e.Handled = true; } catch (InvalidOperationException) { }
        };
    }

    internal static WindowPlacement CapturePlacement(Window window, string id)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var bounds = DesktopGeometry.GetVisibleWindowBounds(handle);
        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        var work = screen.WorkingArea;
        return new(id, screen.DeviceName,
            Math.Clamp((bounds.Left - work.Left) / (double)Math.Max(1, work.Width - bounds.Width), 0, 1),
            Math.Clamp((bounds.Top - work.Top) / (double)Math.Max(1, work.Height - bounds.Height), 0, 1),
            Math.Max(1, window.ActualWidth), Math.Max(1, window.ActualHeight));
    }

    internal static bool TryRestore(Window window, WindowPlacement? placement)
    {
        if (placement is null) return false;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        window.UpdateLayout();
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == placement.MonitorId)
            ?? System.Windows.Forms.Screen.PrimaryScreen!;
        for (var pass = 0; pass < 2; pass++)
        {
            var bounds = DesktopGeometry.GetVisibleWindowBounds(handle);
            if (!GetWindowRect(handle, out var nativeBounds)) return false;
            var work = screen.WorkingArea;
            SetWindowPos(handle, 0,
                work.Left + (int)Math.Round(Math.Clamp(placement.RelativeX, 0, 1) * Math.Max(0, work.Width - bounds.Width)) + nativeBounds.Left - bounds.Left,
                work.Top + (int)Math.Round(Math.Clamp(placement.RelativeY, 0, 1) * Math.Max(0, work.Height - bounds.Height)) + nativeBounds.Top - bounds.Top,
                0, 0, 0x15);
            window.UpdateLayout();
        }
        return true;
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}

internal sealed class OutsideClickDismissBehavior : IDisposable
{
    private readonly Window _window;
    private readonly Action _dismiss;
    private readonly Func<bool> _enabled;
    private readonly HookCallback _callback;
    private readonly bool _installHook;
    private nint _hook;
    private int _generation;
    private bool _disposed;
    internal bool IsMonitoring => _hook != 0;

    internal OutsideClickDismissBehavior(Window window, Action dismiss, Func<bool>? enabled = null, bool installHook = true)
    {
        _window = window; _dismiss = dismiss; _enabled = enabled ?? (() => true); _installHook = installHook;
        _callback = OnMouse;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
        UpdateHook();
    }
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { _generation++; UpdateHook(); }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private void UpdateHook()
    {
        if (_disposed || !_installHook) return;
        if (_window.IsVisible && _hook == 0)
        {
            _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
            if (_hook == 0) System.Diagnostics.Trace.TraceWarning("Outside-click monitor could not be installed. Win32 error: {0}", Marshal.GetLastWin32Error());
        }
        else if (!_window.IsVisible) RemoveHook();
    }
    internal void ProcessPointerDown(int x, int y)
    {
        if (_disposed || !_window.IsVisible || !_enabled() || _window.IsMouseCaptureWithin) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == 0) return;
        if (GetCapture() == handle) return;
        if (IsInsideOwnedPopup(x, y)) return;
        var bounds = DesktopGeometry.GetVisibleWindowBounds(handle);
        var target = GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2);
        if (target == 0 && x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return;
        for (var i = 0; target != 0 && i < 16; i++, target = GetWindow(target, 4))
            if (target == handle) return;
        _dismiss();
    }
    private bool IsInsideOwnedPopup(int x, int y)
    {
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
        {
            if (!source.Dispatcher.CheckAccess() || source.Handle == 0 || source.RootVisual is not { } root || !ContainsOwnedMenu(root)) continue;
            var bounds = DesktopGeometry.GetVisibleWindowBounds(source.Handle);
            if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return true;
        }
        return false;
    }
    private bool ContainsOwnedMenu(DependencyObject root)
    {
        if (root is System.Windows.Controls.ContextMenu { IsOpen: true, PlacementTarget: { } target } && Window.GetWindow(target) == _window)
            return true;
        if (root is not Visual) return false;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsOwnedMenu(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        if (code >= 0 && message.ToInt64() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<NativePoint>(data);
            QueuePointerDown(point.X, point.Y);
        }
        return CallNextHookEx(_hook, code, message, data);
    }
    internal void QueuePointerDown(int x, int y)
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        var generation = _generation;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        { if (generation == _generation && !_disposed) ProcessPointerDown(x, y); }));
    }
    private void RemoveHook() { if (_hook != 0) { _ = UnhookWindowsHookEx(_hook); _hook = 0; } }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; RemoveHook();
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern nint GetCapture();
}
