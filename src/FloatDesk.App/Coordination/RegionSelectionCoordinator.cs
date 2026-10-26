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
    private readonly Func<Action, Task<bool>> _tryScheduleCleanup;
    private readonly object _lifecycleSync = new();
    private readonly Queue<Action> _pendingCleanups = new();
    private bool _disposed;
    private int _activeSelections;
    private TaskCompletionSource? _activeSelectionsDrained;
    private Task _disposeBarrier = Task.CompletedTask;

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
        Func<Action, Task<bool>>? tryScheduleCleanup = null)
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
            lock (_lifecycleSync)
                return _pendingCleanups.Count;
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_lifecycleSync)
                return _disposed;
        }
    }

    internal Task DisposeBarrier
    {
        get
        {
            lock (_lifecycleSync)
                return _disposeBarrier;
        }
    }

    public async Task<PixelRect?> SelectAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        BeginSelectionOperation();
        try
        {
            await FlushPendingCleanupsAsync()
                .ConfigureAwait(true);
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return await SelectCoreAsync(
                desktop,
                diagnostics,
                cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            EndSelectionOperation();
        }
    }

    private async Task<PixelRect?> SelectCoreAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        CancellationToken cancellationToken)
    {
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
            _ = CompleteAfterDispatchFailureAsync(exception);
        }

        async Task CompleteAfterDispatchFailureAsync(Exception exception)
        {
            try
            {
                await ScheduleOrTransferCleanupAsync(CloseAll)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                TransferCleanupOwnership(CloseAll);
            }

            if (cancellationToken.IsCancellationRequested)
                completion.TrySetResult(null);
            else
                completion.TrySetException(exception);
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
        Task activeDrained;
        TaskCompletionSource barrier;
        lock (_lifecycleSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            barrier = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeBarrier = barrier.Task;
            activeDrained = _activeSelections == 0
                ? Task.CompletedTask
                : GetActiveSelectionsDrainedTask();
        }

        _ = DrainForDisposeAsync(activeDrained, barrier);
    }

    private void BeginSelectionOperation()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeSelections++;
        }
    }

    private void EndSelectionOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleSync)
        {
            _activeSelections--;
            if (_activeSelections == 0)
            {
                drained = _activeSelectionsDrained;
                _activeSelectionsDrained = null;
            }
        }
        drained?.TrySetResult();
    }

    private Task GetActiveSelectionsDrainedTask()
    {
        _activeSelectionsDrained ??= new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _activeSelectionsDrained.Task;
    }

    private void ThrowIfDisposed()
    {
        lock (_lifecycleSync)
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task FlushPendingCleanupsAsync()
    {
        while (true)
        {
            Action cleanup;
            lock (_lifecycleSync)
            {
                if (_pendingCleanups.Count == 0)
                    return;
                cleanup = _pendingCleanups.Peek();
            }

            if (!await TryScheduleCleanupAsync(cleanup)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Region selection cleanup could not be completed.");
            }

            RemovePendingCleanup(cleanup);
        }
    }

    private async Task FlushPendingCleanupsForDisposeAsync()
    {
        while (true)
        {
            Action cleanup;
            lock (_lifecycleSync)
            {
                if (_pendingCleanups.Count == 0)
                    return;
                cleanup = _pendingCleanups.Peek();
            }

            if (!await TryScheduleCleanupAsync(cleanup)
                    .ConfigureAwait(false))
            {
                return;
            }

            RemovePendingCleanup(cleanup);
        }
    }

    private async Task ScheduleOrTransferCleanupAsync(Action cleanup)
    {
        if (await TryScheduleCleanupAsync(cleanup).ConfigureAwait(false))
            return;
        TransferCleanupOwnership(cleanup);
    }

    private async Task<bool> TryScheduleCleanupAsync(Action cleanup)
    {
        try
        {
            return await _tryScheduleCleanup(cleanup)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void TransferCleanupOwnership(Action cleanup)
    {
        lock (_lifecycleSync)
        {
            if (_disposed && _activeSelections == 0)
                return;
            _pendingCleanups.Enqueue(cleanup);
        }
    }

    private void RemovePendingCleanup(Action cleanup)
    {
        lock (_lifecycleSync)
        {
            if (_pendingCleanups.Count > 0 &&
                ReferenceEquals(_pendingCleanups.Peek(), cleanup))
            {
                _pendingCleanups.Dequeue();
            }
        }
    }

    private async Task DrainForDisposeAsync(
        Task activeDrained,
        TaskCompletionSource barrier)
    {
        try
        {
            await activeDrained.ConfigureAwait(false);
            await FlushPendingCleanupsForDisposeAsync()
                .ConfigureAwait(false);
        }
        finally
        {
            barrier.TrySetResult();
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

    private static Task<bool> TryScheduleWpfCleanup(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null ||
            dispatcher.HasShutdownStarted ||
            dispatcher.HasShutdownFinished)
        {
            return Task.FromResult(false);
        }

        EventHandler? shutdownStarted = null;
        try
        {
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.FromResult(true);
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            shutdownStarted = (_, _) =>
                completion.TrySetResult(false);
            dispatcher.ShutdownStarted += shutdownStarted;
            var registeredShutdown = shutdownStarted;
            dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    dispatcher.ShutdownStarted -= registeredShutdown;
                    try
                    {
                        action();
                        completion.TrySetResult(true);
                    }
                    catch (Exception)
                    {
                        completion.TrySetResult(false);
                    }
                }));
            return completion.Task;
        }
        catch (Exception)
        {
            if (shutdownStarted is not null)
                dispatcher.ShutdownStarted -= shutdownStarted;
            return Task.FromResult(false);
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
