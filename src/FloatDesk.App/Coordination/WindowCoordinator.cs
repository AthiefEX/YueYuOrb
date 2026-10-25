using System.Windows;
using FloatDesk.Core.Contracts;

namespace FloatDesk.App.Coordination;

public sealed class WindowCoordinator : IWindowCoordinator
{
    private readonly Dictionary<ToolWindow, Func<Window>> _factories;
    private readonly Dictionary<ToolWindow, Window> _windows = [];
    private readonly Dictionary<Window, bool> _captureVisibility = [];
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

    public async Task<IAsyncDisposable> HideForCaptureAsync(
        CancellationToken cancellationToken)
    {
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _captureVisibility.Clear();
            foreach (var window in EnumerateWindows())
            {
                _captureVisibility[window] = window.IsVisible;
                window.Hide();
            }
            System.Windows.Application.Current.MainWindow = _orb;
        });
        await Task.Delay(80, cancellationToken);
        return new RestoreScope(this);
    }

    public void ShowTool(ToolWindow tool)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
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
        if (_orb is not null) yield return _orb;
        if (_quickPanel is not null) yield return _quickPanel;
        foreach (var window in _windows.Values) yield return window;
    }

    private void Restore()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var (window, wasVisible) in _captureVisibility)
            {
                if (window == _quickPanel)
                    continue;
                if (wasVisible && !window.IsVisible)
                    window.Show();
            }
            _captureVisibility.Clear();
            if (_orb is not null)
                System.Windows.Application.Current.MainWindow = _orb;
        });
    }

    private sealed class RestoreScope(WindowCoordinator owner) : IAsyncDisposable
    {
        private WindowCoordinator? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Restore();
            return ValueTask.CompletedTask;
        }
    }
}
