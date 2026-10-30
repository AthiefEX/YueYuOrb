using System.Windows;
using System.Windows.Threading;
using FloatDesk.App.Views;
using Microsoft.Win32;

namespace FloatDesk.App.Brightness;

public sealed class NightModeController : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NightModeState _state = new();
    private readonly List<NightDimWindow> _windows = [];
    private readonly Dictionary<Window, bool> _originalTopmost = [];
    private IDisposable? _lockLease;
    private IDisposable? _subtitleLease;
    private bool _disposed;
    public bool IsEnabled => _state.IsEnabled;
    public event Action? Changed;

    public NightModeController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
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
        if (!_state.IsVisible || _disposed || Application.Current is null) return;
        foreach (Window window in Application.Current.Windows)
        {
            if (window is NightDimWindow || !window.IsVisible) continue;
            _originalTopmost.TryAdd(window, window.Topmost);
            window.Topmost = false;
            window.Topmost = true;
        }
    });
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
        if (_state.IsVisible) RaiseControls();
        else
        {
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
