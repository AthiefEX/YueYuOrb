using System.Windows;
using System.Windows.Threading;
using FloatDesk.Core.Contracts;

namespace FloatDesk.App.Coordination;

public sealed class WindowCoordinator : IWindowCoordinator
{
    private readonly Dictionary<ToolWindow, Func<Window>> _factories;
    private readonly Dictionary<ToolWindow, Window> _windows = [];
    private readonly Dictionary<Window, bool> _captureVisibility = [];
    private readonly HashSet<Window> _captureHiddenWindows = [];
    private Window? _orb;
    private Window? _quickPanel;

    public WindowCoordinator(
        Dictionary<ToolWindow, Func<Window>> factories,
        Window orb,
        Window quickPanel)
    {
        _factories = factories;
        _orb = orb;
        _quickPanel = quickPanel;
    }

    public event Action<Window>? WindowCreated;
    public event Action<Window>? ToolShown;
    public Func<IDisposable>? CapturePauseFactory { get; set; }

    public void RegisterCaptureHiddenWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _captureHiddenWindows.Add(window);
    }

    public void UnregisterCaptureHiddenWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _captureHiddenWindows.Remove(window);
    }

    public async Task<IAsyncDisposable> HideForCaptureAsync(
        CancellationToken cancellationToken)
    {
        var dispatcher = GetDispatcher();
        var pause = await dispatcher.InvokeAsync(() => CapturePauseFactory?.Invoke());
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                _captureVisibility.Clear();
                foreach (var window in EnumerateWindows())
                {
                    _captureVisibility[window] = window.IsVisible;
                    if (window is FloatDesk.App.Views.QuickPanelWindow panel)
                    {
                        panel.PrepareForScreenCapture();
                        continue;
                    }
                    window.Hide();
                }
                if (System.Windows.Application.Current is { } application)
                    application.MainWindow = _orb;
            });
            await Task.Delay(80, cancellationToken);
            return new RestoreScope(this, pause);
        }
        catch
        {
            pause?.Dispose();
            try
            {
                Restore();
            }
            catch
            {
                // Preserve the original hide failure.
            }
            throw;
        }
    }

    public void ShowTool(ToolWindow tool)
    {
        GetDispatcher().Invoke(() =>
        {
            if (!_windows.TryGetValue(tool, out var window))
            {
                window = _factories[tool]();
                _windows[tool] = window;
                WindowCreated?.Invoke(window);
            }
            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            ToolShown?.Invoke(window);
        });
    }

    public void ShowQuickPanel()
    {
        if (_quickPanel is null)
            return;
        if (!_quickPanel.IsVisible)
            _quickPanel.Show();
        _quickPanel.Activate();
    }

    public void HideQuickPanel()
    {
        if (_quickPanel is FloatDesk.App.Views.QuickPanelWindow panel)
            panel.HideAnimated();
        else
            _quickPanel?.Hide();
    }

    public IReadOnlyList<Window> ToolWindows => _windows.Values.ToArray();

    private IEnumerable<Window> EnumerateWindows()
    {
        var seen = new HashSet<Window>();
        if (_orb is not null && seen.Add(_orb))
            yield return _orb;
        if (_quickPanel is not null && seen.Add(_quickPanel))
            yield return _quickPanel;
        foreach (var window in _windows.Values)
        {
            if (seen.Add(window))
                yield return window;
        }
        foreach (var window in _captureHiddenWindows)
        {
            if (seen.Add(window))
                yield return window;
        }
    }

    private void Restore()
    {
        GetDispatcher().Invoke(() =>
        {
            foreach (var (window, wasVisible) in _captureVisibility)
            {
                if (window == _quickPanel)
                    continue;
                if (window is FloatDesk.App.Views.TranslationOverlayWindow
                    overlay && !overlay.CanRestoreAfterCapture)
                {
                    continue;
                }
                if (wasVisible && !window.IsVisible)
                    window.Show();
            }
            _captureVisibility.Clear();
            if (_orb is not null &&
                System.Windows.Application.Current is { } application)
            {
                application.MainWindow = _orb;
            }
        });
    }

    private Dispatcher GetDispatcher() =>
        _orb?.Dispatcher
        ?? System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;

    private sealed class RestoreScope(WindowCoordinator owner, IDisposable? pause) : IAsyncDisposable
    {
        private WindowCoordinator? _owner = owner;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner)
                try { owner.Restore(); } finally { pause?.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}
