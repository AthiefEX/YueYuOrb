using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using FloatDesk.App.Views;
using Microsoft.Win32;

namespace FloatDesk.App.Brightness;

public sealed class NightModeController : IDisposable
{
    internal static bool KeepAboveMask(Type windowType) => !typeof(OrbWindow).IsAssignableFrom(windowType);
    private readonly Dispatcher _dispatcher;
    private readonly NightModeState _state = new();
    private readonly List<NightDimWindow> _windows = [];
    private readonly Dictionary<Window, bool> _originalTopmost = [];
    private IDisposable? _lockLease;
    private IDisposable? _subtitleLease;
    private bool _disposed;
    private readonly DispatcherTimer _stackTimer;
    private readonly WinEventCallback _windowEventCallback;
    private readonly List<nint> _stackHooks = [];
    private bool _maintainingStack;
    public bool IsEnabled => _state.IsEnabled;
    public event Action? Changed;

    public NightModeController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _windowEventCallback = OnWindowEvent;
        _stackTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        _stackTimer.Tick += (_, _) => MaintainStacking();
        _state.Changed += Update;
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }
    public void SetEnabled(bool value) => _dispatcher.Invoke(() => { if (!_disposed) _state.SetEnabled(value); });
    public void SetDimmingPercent(int value) => _dispatcher.Invoke(() => { if (!_disposed) _state.SetDimmingPercent(value); });
    public IDisposable Suspend() => _dispatcher.Invoke(() => (IDisposable)new DispatcherLease(_dispatcher, _state.Suspend()));
    public void SetSubtitleSessionActive(bool active) => _dispatcher.Invoke(() =>
    {
        if (active && _subtitleLease is null) _subtitleLease = _state.Suspend();
        if (!active) { _subtitleLease?.Dispose(); _subtitleLease = null; }
    });
    public void RefreshDisplays() => _dispatcher.Invoke(() =>
    {
        foreach (var window in _windows) window.Close();
        _windows.Clear();
        Update();
    });
    private void OnDisplaysChanged(object? sender, EventArgs args) => _dispatcher.BeginInvoke(() => { if (!_disposed) RefreshDisplays(); });
    private void OnSessionSwitch(object sender, SessionSwitchEventArgs args) => _dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (args.Reason == SessionSwitchReason.SessionLock && _lockLease is null) _lockLease = _state.Suspend();
        if (args.Reason == SessionSwitchReason.SessionUnlock) { _lockLease?.Dispose(); _lockLease = null; RefreshDisplays(); }
    });
    public void RaiseControls() => _dispatcher.Invoke(() =>
    {
        if (!_state.IsVisible || _disposed || Application.Current is not { } app || !app.Dispatcher.CheckAccess()) return;
        foreach (Window window in app.Windows)
        {
            if (window is NightDimWindow || !window.IsVisible || !KeepAboveMask(window.GetType())) continue;
            _originalTopmost.TryAdd(window, window.Topmost);
            window.Topmost = true;
            RaiseWindow(new WindowInteropHelper(window).Handle);
        }
    });
    private void MaintainStacking()
    {
        if (_disposed || !_state.IsVisible || _maintainingStack) return;
        _maintainingStack = true;
        try { MaintainStackingCore(); }
        finally { _maintainingStack = false; }
    }
    private void MaintainStackingCore()
    {
        var controls = (Application.Current is { } app && app.Dispatcher.CheckAccess() ? app.Windows.Cast<Window>() : [])
            .Where(w => w is not NightDimWindow && w.IsVisible && KeepAboveMask(w.GetType()))
            .Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        var masks = _windows.Select(w => new WindowInteropHelper(w).Handle).ToHashSet();
        foreach (var mask in masks)
        {
            if (!GetWindowRect(mask, out var bounds)) continue;
            for (var above = GetWindow(mask, 3); above != 0; above = GetWindow(above, 3))
            {
                if (masks.Contains(above) || controls.Contains(above) || !IsWindowVisible(above) ||
                    !GetWindowRect(above, out var rect) || !bounds.Intersects(rect)) continue;
                // A newly shown topmost popup can cover a mask which was already topmost.
                // Reorder only our own windows; never focus, move or inspect the popup.
                foreach (var window in _windows) window.RaiseWithoutActivation();
                RaiseControls();
                return;
            }
        }
    }
    private void StartWindowEvents()
    {
        if (_stackHooks.Count != 0) return;
        // OUTOFCONTEXT callbacks are delivered on this dispatcher's thread. They only
        // read window geometry and reorder our masks; no target process is injected.
        foreach (var eventId in new uint[] { 0x0003, 0x8002, 0x8004, 0x800B })
        {
            var hook = SetWinEventHook(eventId, eventId, 0, _windowEventCallback, 0, 0, 0);
            if (hook != 0) _stackHooks.Add(hook);
        }
    }
    private void StopWindowEvents()
    {
        foreach (var hook in _stackHooks) UnhookWinEvent(hook);
        _stackHooks.Clear();
    }
    private void OnWindowEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed || !_state.IsVisible || _maintainingStack || !_dispatcher.CheckAccess()) return;
        if (childId != 0 || (objectId != 0 && eventId != 0x8004)) return;
        if (window != 0 && eventId != 0x8004 &&
            (!IsWindowVisible(window) || GetAncestor(window, 2) != window)) return;
        // Do not queue behind rendering or wait for the fallback timer.
        MaintainStacking();
    }
    private delegate void WinEventCallback(nint hook, uint eventId, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minEvent, uint maxEvent, nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    private static void RaiseWindow(nint handle) => SetWindowPos(handle, new nint(-1), 0, 0, 0, 0, 0x13);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Intersects(NativeRect other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
    private void Update()
    {
        if (_disposed) return;
        if (_state.IsVisible && _windows.Count == 0)
            foreach (var bounds in System.Windows.Forms.Screen.AllScreens.Select(s => s.Bounds).Distinct())
            {
                var window = new NightDimWindow { Opacity = _state.DimmingPercent / 100d };
                _windows.Add(window);
                window.SetPhysicalBounds(bounds);
            }
        foreach (var window in _windows)
        {
            window.Opacity = _state.DimmingPercent / 100d;
            if (_state.IsVisible) window.Show(); else window.Hide();
        }
        if (_state.IsVisible)
        {
            StartWindowEvents();
            _stackTimer.Start();
            MaintainStacking();
            RaiseControls();
        }
        else
        {
            _stackTimer.Stop();
            StopWindowEvents();
            foreach (var (window, topmost) in _originalTopmost) if (window.IsLoaded) window.Topmost = topmost;
            _originalTopmost.Clear();
        }
        Changed?.Invoke();
    }
    public void Dispose() => _dispatcher.Invoke(() =>
    {
        if (_disposed) return;
        _state.SetEnabled(false);
        _disposed = true;
        _stackTimer.Stop();
        StopWindowEvents();
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        foreach (var window in _windows) window.Close();
        _windows.Clear();
    });
    private sealed class DispatcherLease(Dispatcher dispatcher, IDisposable inner) : IDisposable
    {
        private IDisposable? _inner = inner;
        public void Dispose() { var lease = Interlocked.Exchange(ref _inner, null); if (lease is not null) dispatcher.Invoke(lease.Dispose); }
    }
}
