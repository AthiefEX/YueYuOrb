using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatDesk.App.Capture;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Coordination;

public sealed class RegionSelectionCoordinator : IDisposable
{
    private readonly Func<IReadOnlyList<DisplayMonitor>> _getMonitors;
    private readonly Func<CaptureFrame, BitmapSource> _bitmapFactory;
    private readonly Func<
        DisplayMonitor,
        PixelRect,
        BitmapSource,
        IRegionSelectionSurface> _surfaceFactory;
    private readonly Action<Action> _dispatch;
    private readonly Func<Action, bool> _tryScheduleCleanup;
    private readonly object _pendingCleanupSync = new();
    private readonly List<Action> _pendingCleanups = [];

    public RegionSelectionCoordinator()
        : this(
            DisplayTopology.GetMonitors,
            CaptureExportService.ToBitmap,
            static (monitor, frozenBounds, bitmap) =>
                new CaptureOverlaySurface(new CaptureOverlayWindow(
                    monitor,
                    frozenBounds,
                    bitmap)),
            DispatchToWpf,
            TryScheduleWpfCleanup)
    {
    }

    internal RegionSelectionCoordinator(
        Func<IReadOnlyList<DisplayMonitor>> getMonitors,
        Func<CaptureFrame, BitmapSource> bitmapFactory,
        Func<DisplayMonitor, PixelRect, BitmapSource, IRegionSelectionSurface> surfaceFactory,
        Action<Action>? dispatch = null,
        Func<Action, bool>? tryScheduleCleanup = null)
    {
        _getMonitors = getMonitors;
        _bitmapFactory = bitmapFactory;
        _surfaceFactory = surfaceFactory;
        _dispatch = dispatch ?? DispatchToWpf;
        _tryScheduleCleanup = tryScheduleCleanup ?? TryScheduleWpfCleanup;
    }

    internal int PendingCleanupCount
    {
        get
        {
            lock (_pendingCleanupSync)
                return _pendingCleanups.Count;
        }
    }

    public async Task<PixelRect?> SelectAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var completion = new TaskCompletionSource<PixelRect?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var surfaces = new List<IRegionSelectionSurface>();
        var shown = new HashSet<IRegionSelectionSurface>();
        var closed = new HashSet<IRegionSelectionSurface>();
        var completionClaimed = 0;
        var closeStarted = false;
        PixelPoint? anchor = null;
        var selection = new PixelRect(0, 0, 0, 0);

        void CloseAll()
        {
            foreach (var surface in surfaces)
            {
                if (!closed.Add(surface))
                    continue;
                if (shown.Contains(surface))
                {
                    try
                    {
                        surface.Hide();
                    }
                    catch (Exception)
                    {
                    }
                }
                try
                {
                    surface.CloseWithoutCancelling();
                }
                catch (Exception)
                {
                }
            }
        }

        bool TryClaimCompletion() =>
            Interlocked.Exchange(ref completionClaimed, 1) == 0;

        void Complete(PixelRect? result)
        {
            if (!TryClaimCompletion())
                return;
            closeStarted = true;
            try
            {
                CloseAll();
            }
            finally
            {
                completion.TrySetResult(result);
            }
        }

        void Fail(Exception exception)
        {
            if (!TryClaimCompletion())
                return;
            closeStarted = true;
            try
            {
                CloseAll();
            }
            finally
            {
                completion.TrySetException(exception);
            }
        }

        void FailOrCancel(Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                Complete(null);
            else
                Fail(exception);
        }

        void HandleDispatchFailure(Exception exception)
        {
            if (!TryClaimCompletion())
                return;

            closeStarted = true;
            try
            {
                if (!TryScheduleCleanup(CloseAll))
                    TransferCleanupOwnership(CloseAll);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                    completion.TrySetResult(null);
                else
                    completion.TrySetException(exception);
            }
        }

        bool TryScheduleCleanup(Action cleanup)
        {
            try
            {
                return _tryScheduleCleanup(cleanup);
            }
            catch (Exception)
            {
                return false;
            }
        }

        void TransferCleanupOwnership(Action cleanup)
        {
            lock (_pendingCleanupSync)
                _pendingCleanups.Add(cleanup);
        }

        void HandleSurfaceEvent(Action action)
        {
            try
            {
                _dispatch(() =>
                {
                    if (completion.Task.IsCompleted)
                        return;
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        FailOrCancel(exception);
                    }
                });
            }
            catch (Exception exception)
            {
                HandleDispatchFailure(exception);
            }
        }

        void UpdateSelection(PixelPoint current)
        {
            if (closeStarted || anchor is null)
                return;
            selection = SelectionGeometry.FromPoints(
                anchor.Value,
                current);
            foreach (var surface in surfaces)
            {
                if (closeStarted)
                    break;
                surface.ShowSelection(selection);
            }
        }

        void AttachHandlers()
        {
            foreach (var surface in surfaces)
            {
                surface.DragStarted += point => HandleSurfaceEvent(() =>
                {
                    if (!closeStarted)
                    {
                        anchor = point;
                        diagnostics?.Invoke(
                            "capture.region.drag.started",
                            new Dictionary<string, object?>
                            {
                                ["anchor"] = point.ToString()
                            });
                        UpdateSelection(point);
                    }
                });
                surface.DragMoved += point => HandleSurfaceEvent(() =>
                {
                    UpdateSelection(point);
                });
                surface.DragCompleted += point => HandleSurfaceEvent(() =>
                {
                    UpdateSelection(point);
                    diagnostics?.Invoke(
                        "capture.region.drag.completed",
                        new Dictionary<string, object?>
                        {
                            ["selection"] = selection.ToString()
                        });
                });
                surface.CancelRequested += () => HandleSurfaceEvent(() =>
                {
                    try
                    {
                        diagnostics?.Invoke(
                            "capture.region.cancelled",
                            null);
                    }
                    catch (Exception)
                    {
                    }
                    Complete(null);
                });
                surface.ConfirmRequested += () => HandleSurfaceEvent(() =>
                {
                    diagnostics?.Invoke(
                        "capture.region.confirmed",
                        new Dictionary<string, object?>
                        {
                            ["selection"] = selection.ToString(),
                            ["usable"] =
                                SelectionGeometry.IsUsable(selection)
                        });
                    if (SelectionGeometry.IsUsable(selection))
                        Complete(selection);
                });
                surface.NudgeRequested += (dx, dy) => HandleSurfaceEvent(() =>
                {
                    if (closeStarted ||
                        !SelectionGeometry.IsUsable(selection))
                    {
                        return;
                    }
                    selection = ApplyNudge(
                        selection,
                        desktop.Bounds,
                        dx,
                        dy);
                    foreach (var current in surfaces)
                        current.ShowSelection(selection);
                });
            }
        }

        void BeginSelection()
        {
            if (completion.Task.IsCompleted ||
                cancellationToken.IsCancellationRequested)
            {
                Complete(null);
                return;
            }

            try
            {
                var bitmap = _bitmapFactory(desktop);
                var monitors = _getMonitors();
                diagnostics?.Invoke(
                    "capture.region.overlays",
                    new Dictionary<string, object?>
                    {
                        ["count"] = monitors.Count,
                        ["frozenBounds"] = desktop.Bounds.ToString()
                    });

                foreach (var monitor in monitors)
                {
                    if (completion.Task.IsCompleted ||
                        cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    surfaces.Add(_surfaceFactory(
                        monitor,
                        desktop.Bounds,
                        bitmap));
                    if (completion.Task.IsCompleted ||
                        cancellationToken.IsCancellationRequested)
                    {
                        closeStarted = true;
                        CloseAll();
                        completion.TrySetResult(null);
                        return;
                    }
                }

                if (completion.Task.IsCompleted ||
                    cancellationToken.IsCancellationRequested ||
                    surfaces.Count == 0)
                {
                    Complete(null);
                    return;
                }

                AttachHandlers();
                foreach (var surface in surfaces)
                {
                    if (closeStarted || completion.Task.IsCompleted)
                        break;
                    shown.Add(surface);
                    surface.Show();
                    if (closeStarted || completion.Task.IsCompleted)
                        return;
                    surface.PlaceWindow();
                }
                if (!closeStarted && !completion.Task.IsCompleted)
                {
                    surfaces[0].Activate();
                    surfaces[0].Focus();
                }
            }
            catch (Exception exception)
            {
                if (completion.Task.IsCompleted)
                    return;
                FailOrCancel(exception);
            }
        }

        void Cancel()
        {
            try
            {
                _dispatch(() => Complete(null));
            }
            catch (Exception exception)
            {
                HandleDispatchFailure(exception);
            }
        }

        using var registration = cancellationToken.Register(Cancel);
        if (cancellationToken.IsCancellationRequested)
            Cancel();
        else
        {
            try
            {
                _dispatch(BeginSelection);
            }
            catch (Exception exception)
            {
                HandleDispatchFailure(exception);
            }
        }

        return await completion.Task.ConfigureAwait(true);
    }

    public void Dispose()
    {
        Action[] pending;
        lock (_pendingCleanupSync)
        {
            pending = _pendingCleanups.ToArray();
            _pendingCleanups.Clear();
        }

        foreach (var cleanup in pending)
        {
            if (TryScheduleCleanup(cleanup))
                continue;
            lock (_pendingCleanupSync)
                _pendingCleanups.Add(cleanup);
        }
    }

    private bool TryScheduleCleanup(Action cleanup)
    {
        try
        {
            return _tryScheduleCleanup(cleanup);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static PixelRect ApplyNudge(
        PixelRect selection,
        PixelRect frozenBounds,
        int dx,
        int dy)
    {
        var left = Math.Clamp(
            selection.Left + dx,
            frozenBounds.Left,
            frozenBounds.Right - selection.Width);
        var top = Math.Clamp(
            selection.Top + dy,
            frozenBounds.Top,
            frozenBounds.Bottom - selection.Height);
        return new PixelRect(left, top, selection.Width, selection.Height);
    }

    private static void DispatchToWpf(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.Invoke(action);
    }

    private static bool TryScheduleWpfCleanup(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return true;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Send, action);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal interface IRegionSelectionSurface
{
    event Action<PixelPoint>? DragStarted;
    event Action<PixelPoint>? DragMoved;
    event Action<PixelPoint>? DragCompleted;
    event Action? CancelRequested;
    event Action? ConfirmRequested;
    event Action<int, int>? NudgeRequested;

    void ShowSelection(PixelRect selection);
    void Show();
    void PlaceWindow();
    void Activate();
    void Focus();
    void Hide();
    void CloseWithoutCancelling();
}

internal sealed class CaptureOverlaySurface(CaptureOverlayWindow window)
    : IRegionSelectionSurface
{
    public event Action<PixelPoint>? DragStarted
    {
        add => window.DragStarted += value;
        remove => window.DragStarted -= value;
    }

    public event Action<PixelPoint>? DragMoved
    {
        add => window.DragMoved += value;
        remove => window.DragMoved -= value;
    }

    public event Action<PixelPoint>? DragCompleted
    {
        add => window.DragCompleted += value;
        remove => window.DragCompleted -= value;
    }

    public event Action? CancelRequested
    {
        add => window.CancelRequested += value;
        remove => window.CancelRequested -= value;
    }

    public event Action? ConfirmRequested
    {
        add => window.ConfirmRequested += value;
        remove => window.ConfirmRequested -= value;
    }

    public event Action<int, int>? NudgeRequested
    {
        add => window.NudgeRequested += value;
        remove => window.NudgeRequested -= value;
    }

    public void ShowSelection(PixelRect selection) =>
        window.ShowSelection(selection);
    public void Show() => window.Show();
    public void PlaceWindow() => window.PlaceWindow();
    public void Activate() => window.Activate();
    public void Focus() => window.Focus();
    public void Hide() => window.Hide();
    public void CloseWithoutCancelling() => window.CloseWithoutCancelling();
}
