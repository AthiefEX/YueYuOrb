using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatDesk.App.Capture;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Coordination;

public sealed class RegionSelectionCoordinator : IDisposable, IAsyncDisposable
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
    private readonly Func<bool> _isOwnerThread;
    private readonly object _lifecycleSync = new();
    private readonly Queue<Action> _pendingCleanups = new();
    private readonly HashSet<SelectionOperation> _activeOperations = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private bool _disposeRequested;
    private bool _shutdownCompleted;
    private TaskCompletionSource? _activeSelectionsDrained;
    private TaskCompletionSource<RegionSelectionShutdownResult>? _shutdownCompletion;

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
        Func<Action, Task<bool>>? tryScheduleCleanup = null,
        Func<bool>? isOwnerThread = null)
    {
        _getMonitors = getMonitors;
        _bitmapFactory = bitmapFactory;
        _surfaceFactory = surfaceFactory;
        _dispatch = dispatch ?? DispatchToWpf;
        _tryScheduleCleanup = tryScheduleCleanup ?? TryScheduleWpfCleanup;
        _isOwnerThread = isOwnerThread ?? IsWpfOwnerThread;
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
                return _disposeRequested;
        }
    }

    internal Task DisposeBarrier
    {
        get
        {
            lock (_lifecycleSync)
                return _shutdownCompletion?.Task ?? Task.CompletedTask;
        }
    }

    public async Task<PixelRect?> SelectAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        var operation = BeginSelectionOperation();
        using var lifetimeToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var effectiveCancellationToken = lifetimeToken.Token;
        try
        {
            await FlushPendingCleanupsAsync()
                .ConfigureAwait(true);
            ThrowIfDisposed();
            effectiveCancellationToken.ThrowIfCancellationRequested();
            return await SelectCoreAsync(
                desktop,
                diagnostics,
                operation,
                effectiveCancellationToken).ConfigureAwait(true);
        }
        finally
        {
            EndSelectionOperation(operation);
        }
    }

    private async Task<PixelRect?> SelectCoreAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        SelectionOperation operation,
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

        bool ShutdownRequested() =>
            IsDisposed ||
            cancellationToken.IsCancellationRequested;

        void FinishOperation()
        {
            EndSelectionOperation(operation);
        }

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
                FinishOperation();
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
                FinishOperation();
            }
        }

        void FailOrCancel(Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
                Complete(null);
            else
                Fail(exception);
        }

        operation.SetShutdown(() => Complete(null));

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
            FinishOperation();
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
                ShutdownRequested())
            {
                Complete(null);
                return;
            }

            try
            {
                if (ShutdownRequested())
                {
                    Complete(null);
                    return;
                }
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
                        ShutdownRequested())
                    {
                        break;
                    }
                    surfaces.Add(_surfaceFactory(
                        monitor,
                        desktop.Bounds,
                        bitmap));
                    if (completion.Task.IsCompleted ||
                        ShutdownRequested())
                    {
                        closeStarted = true;
                        CloseAll();
                        completion.TrySetResult(null);
                        FinishOperation();
                        return;
                    }
                }

                if (completion.Task.IsCompleted ||
                    ShutdownRequested() ||
                    surfaces.Count == 0)
                {
                    Complete(null);
                    return;
                }

                AttachHandlers();
                foreach (var surface in surfaces)
                {
                    if (closeStarted ||
                        completion.Task.IsCompleted ||
                        ShutdownRequested())
                    {
                        break;
                    }
                    shown.Add(surface);
                    surface.Show();
                    if (closeStarted || completion.Task.IsCompleted)
                        return;
                    if (ShutdownRequested())
                    {
                        Complete(null);
                        return;
                    }
                    surface.PlaceWindow();
                }
                if (!closeStarted &&
                    !completion.Task.IsCompleted &&
                    !ShutdownRequested())
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
            Complete(null);
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
        var activeDrained = RequestDispose();
        if (_isOwnerThread())
        {
            var result = TryShutdownOnOwnerThread();
            if (result.Drained)
            {
                GC.SuppressFinalize(this);
                return;
            }
        }

        _ = DrainForDisposeAsync(activeDrained);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        var result = await ShutdownAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
        if (!result.Drained)
        {
            throw new InvalidOperationException(
                "Region selection cleanup could not be completed; " +
                $"{result.PendingCleanupCount} cleanup operation(s) remain.");
        }
    }

    internal async Task<RegionSelectionShutdownResult> ShutdownAsync()
    {
        var activeDrained = RequestDispose();
        await _drainGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryGetShutdownResult(out var completed))
                return completed;

            await activeDrained.ConfigureAwait(false);
            if (!await FlushPendingCleanupsForDisposeAsync()
                    .ConfigureAwait(false))
            {
                return UndrainedResult();
            }

            return TryCompleteShutdown(out var result)
                ? result
                : UndrainedResult();
        }
        catch (Exception)
        {
            return UndrainedResult();
        }
        finally
        {
            _drainGate.Release();
        }
    }

    internal RegionSelectionShutdownResult ShutdownOnOwnerThread()
    {
        if (!_isOwnerThread())
        {
            throw new InvalidOperationException(
                "Region selection shutdown must run on the UI owner thread.");
        }

        RequestDispose();
        return TryShutdownOnOwnerThread();
    }

    private SelectionOperation BeginSelectionOperation()
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            var operation = new SelectionOperation();
            _activeOperations.Add(operation);
            return operation;
        }
    }

    private void EndSelectionOperation(SelectionOperation operation)
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleSync)
        {
            operation.MarkStopped();
            if (!_activeOperations.Remove(operation))
                return;
            if (_activeOperations.Count == 0)
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
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
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

    private async Task<bool> FlushPendingCleanupsForDisposeAsync()
    {
        while (true)
        {
            Action cleanup;
            lock (_lifecycleSync)
            {
                if (_pendingCleanups.Count == 0)
                    return true;
                cleanup = _pendingCleanups.Peek();
            }

            if (!await TryScheduleCleanupAsync(cleanup)
                    .ConfigureAwait(false))
            {
                return false;
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
            if (_shutdownCompleted ||
                _pendingCleanups.Contains(cleanup))
            {
                return;
            }
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

    private async Task DrainForDisposeAsync(Task activeDrained)
    {
        await _drainGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryGetShutdownResult(out _))
                return;
            await activeDrained.ConfigureAwait(false);
            if (await FlushPendingCleanupsForDisposeAsync()
                    .ConfigureAwait(false))
            {
                _ = TryCompleteShutdown(out _);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private Task RequestDispose()
    {
        Task activeDrained;
        var cancelLifetime = false;
        lock (_lifecycleSync)
        {
            if (!_disposeRequested)
            {
                _disposeRequested = true;
                _shutdownCompletion = new TaskCompletionSource<
                    RegionSelectionShutdownResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                cancelLifetime = true;
            }

            if (_shutdownCompleted)
                return Task.CompletedTask;
            activeDrained = _activeOperations.Count == 0
                ? Task.CompletedTask
                : GetActiveSelectionsDrainedTask();
        }

        if (cancelLifetime)
            _lifetime.Cancel();
        return activeDrained;
    }

    private RegionSelectionShutdownResult TryShutdownOnOwnerThread()
    {
        if (!_drainGate.Wait(0))
            return UndrainedResult();

        try
        {
            SelectionOperation[] operations;
            lock (_lifecycleSync)
                operations = [.. _activeOperations];
            foreach (var operation in operations)
                operation.ShutdownFromOwner();

            while (true)
            {
                Action cleanup;
                lock (_lifecycleSync)
                {
                    if (_pendingCleanups.Count == 0)
                        break;
                    cleanup = _pendingCleanups.Peek();
                }

                try
                {
                    cleanup();
                }
                catch (Exception)
                {
                    return UndrainedResult();
                }
                RemovePendingCleanup(cleanup);
            }

            return TryCompleteShutdown(out var result)
                ? result
                : UndrainedResult();
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private bool TryCompleteShutdown(
        out RegionSelectionShutdownResult result)
    {
        TaskCompletionSource<RegionSelectionShutdownResult>? completion = null;
        lock (_lifecycleSync)
        {
            if (_shutdownCompleted)
            {
                result = new RegionSelectionShutdownResult(true, 0);
                return true;
            }

            if (!_disposeRequested ||
                _activeOperations.Count != 0 ||
                _pendingCleanups.Count != 0)
            {
                result = new RegionSelectionShutdownResult(
                    false,
                    _pendingCleanups.Count);
                return false;
            }

            _shutdownCompleted = true;
            result = new RegionSelectionShutdownResult(true, 0);
            completion = _shutdownCompletion;
        }

        completion?.TrySetResult(result);
        return true;
    }

    private bool TryGetShutdownResult(
        out RegionSelectionShutdownResult result)
    {
        lock (_lifecycleSync)
        {
            if (_shutdownCompleted)
            {
                result = new RegionSelectionShutdownResult(true, 0);
                return true;
            }
        }

        result = default;
        return false;
    }

    private RegionSelectionShutdownResult UndrainedResult()
    {
        lock (_lifecycleSync)
        {
            return new RegionSelectionShutdownResult(
                _shutdownCompleted,
                _pendingCleanups.Count);
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

    private static bool IsWpfOwnerThread() =>
        System.Windows.Application.Current?.Dispatcher.CheckAccess() == true;

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
            if (dispatcher.HasShutdownStarted ||
                dispatcher.HasShutdownFinished)
            {
                dispatcher.ShutdownStarted -= registeredShutdown;
                return Task.FromResult(false);
            }
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

    private sealed class SelectionOperation
    {
        private Action? _shutdown;
        private int _stopped;
        private int _shutdownRequested;

        public void SetShutdown(Action shutdown)
        {
            _shutdown = shutdown;
            if (Volatile.Read(ref _shutdownRequested) != 0 &&
                Volatile.Read(ref _stopped) == 0)
            {
                shutdown();
            }
        }

        public void ShutdownFromOwner()
        {
            Volatile.Write(ref _shutdownRequested, 1);
            if (Volatile.Read(ref _stopped) == 0)
                _shutdown?.Invoke();
        }

        public void MarkStopped() => Interlocked.Exchange(ref _stopped, 1);
    }
}

internal readonly record struct RegionSelectionShutdownResult(
    bool Drained,
    int PendingCleanupCount);

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
