using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using FloatDesk.App.Coordination;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Ui.Tests;

public sealed class RegionSelectionCoordinatorTests
{
    [Fact]
    public async Task DragNudgeAndConfirmReturnClampedSelectionWithDiagnostics()
    {
        var surface = new FakeRegionSelectionSurface();
        var diagnostics = new List<(string Name, IReadOnlyDictionary<string, object?>? Properties)>();
        var coordinator = CreateCoordinator(surface);
        using var desktop = DesktopFrame();

        var selectionTask = coordinator.SelectAsync(
            desktop,
            (name, properties) => diagnostics.Add((name, properties)),
            CancellationToken.None);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(2));

        surface.RaiseDragStarted(new PixelPoint(40, 40));
        surface.RaiseDragMoved(new PixelPoint(90, 90));
        surface.RaiseDragCompleted(new PixelPoint(90, 90));
        surface.RaiseNudgeRequested(20, 20);
        surface.RaiseConfirmRequested();

        var selection = await selectionTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(new PixelRect(50, 50, 50, 50), selection);
        Assert.Contains(
            diagnostics,
            item => item.Name == "capture.region.overlays");
        Assert.Contains(
            diagnostics,
            item => item.Name == "capture.region.drag.started");
        Assert.Contains(
            diagnostics,
            item => item.Name == "capture.region.drag.completed");
        Assert.Contains(
            diagnostics,
            item => item.Name == "capture.region.confirmed");
        Assert.Contains(surface.Selections, item => item == new PixelRect(50, 50, 50, 50));
        Assert.True(surface.Hidden);
        Assert.True(surface.ClosedWithoutCancellation);
    }

    [Fact]
    public async Task UnusableConfirmKeepsSelectorOpenUntilCancel()
    {
        var surface = new FakeRegionSelectionSurface();
        var diagnostics = new List<(string Name, IReadOnlyDictionary<string, object?>? Properties)>();
        var coordinator = CreateCoordinator(surface);
        using var desktop = DesktopFrame();

        var selectionTask = coordinator.SelectAsync(
            desktop,
            (name, properties) => diagnostics.Add((name, properties)),
            CancellationToken.None);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(2));

        surface.RaiseConfirmRequested();

        Assert.False(selectionTask.IsCompleted);
        Assert.Contains(
            diagnostics,
            item =>
                item.Name == "capture.region.confirmed" &&
                item.Properties?["usable"] is false);

        surface.RaiseCancelRequested();
        Assert.Null(await selectionTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Contains(
            diagnostics,
            item => item.Name == "capture.region.cancelled");
        Assert.True(surface.Hidden);
        Assert.True(surface.ClosedWithoutCancellation);
    }

    [Fact]
    public async Task CancellationClosesSelectionAndReturnsNull()
    {
        var surface = new FakeRegionSelectionSurface();
        var coordinator = CreateCoordinator(surface);
        using var desktop = DesktopFrame();
        using var cancellation = new CancellationTokenSource();

        var selectionTask = coordinator.SelectAsync(
            desktop,
            null,
            cancellation.Token);
        await surface.Shown.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        Assert.Null(await selectionTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(surface.Hidden);
        Assert.True(surface.ClosedWithoutCancellation);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancellationDuringSurfaceCreationNeverShowsClosedSurface()
    {
        var surface = new FakeRegionSelectionSurface();
        using var desktop = DesktopFrame();
        using var cancellation = new CancellationTokenSource();
        var monitor = Monitor();
        var coordinator = new RegionSelectionCoordinator(
            () => [monitor],
            _ => null!,
            (_, _, _) =>
            {
                cancellation.Cancel();
                return surface;
            });

        var selection = await coordinator.SelectAsync(
            desktop,
            null,
            cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(selection);
        Assert.Equal(0, surface.ShowCount);
        Assert.False(surface.Hidden);
        Assert.Equal(1, surface.CloseCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task OffThreadCancellationClosesEverySurfaceOnDispatcher()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var first = new FakeRegionSelectionSurface();
        var second = new FakeRegionSelectionSurface();
        var surfaces = new Queue<FakeRegionSelectionSurface>(
            [first, second]);
        using var desktop = DesktopFrame();
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            [Monitor(@"\\.\DISPLAY1"), Monitor(@"\\.\DISPLAY2", 100)],
            surfaces,
            dispatcher.Dispatch);

        var selectionTask = coordinator.SelectAsync(
            desktop,
            null,
            cancellation.Token);
        await Task.WhenAll(first.Shown.Task, second.Shown.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        Assert.Null(await selectionTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(first.Hidden);
        Assert.True(second.Hidden);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
        Assert.All(
            first.SurfaceThreadIds.Concat(second.SurfaceThreadIds),
            threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    [Fact(Timeout = 10_000)]
    public async Task MidShowFailureClosesEveryCreatedSurfaceAndPropagates()
    {
        var first = new FakeRegionSelectionSurface();
        var second = new FakeRegionSelectionSurface
        {
            ShowException = new InvalidOperationException("show failed")
        };
        var surfaces = new Queue<FakeRegionSelectionSurface>(
            [first, second]);
        using var desktop = DesktopFrame();
        var coordinator = CreateCoordinator(
            [Monitor(@"\\.\DISPLAY1"), Monitor(@"\\.\DISPLAY2", 100)],
            surfaces);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.SelectAsync(
                desktop,
                null,
                CancellationToken.None));

        Assert.Equal("show failed", exception.Message);
        Assert.True(first.Hidden);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task DispatchFailureStillClosesEverySurfaceOnDispatcher()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var first = new FakeRegionSelectionSurface();
        var second = new FakeRegionSelectionSurface();
        var surfaces = new Queue<FakeRegionSelectionSurface>(
            [first, second]);
        using var desktop = DesktopFrame();
        var dispatchCount = 0;
        var coordinator = CreateCoordinator(
            [Monitor(@"\\.\DISPLAY1"), Monitor(@"\\.\DISPLAY2", 100)],
            surfaces,
            action =>
            {
                if (Interlocked.Increment(ref dispatchCount) == 2)
                    throw new InvalidOperationException("dispatch failed");
                dispatcher.Dispatch(action);
            });

        var selectionTask = coordinator.SelectAsync(
            desktop,
            null,
            CancellationToken.None);
        await Task.WhenAll(first.Shown.Task, second.Shown.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));

        first.RaiseDragStarted(new PixelPoint(10, 10));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => selectionTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("dispatch failed", exception.Message);
        Assert.True(first.Hidden);
        Assert.True(second.Hidden);
        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
        Assert.All(
            first.SurfaceThreadIds.Concat(second.SurfaceThreadIds),
            threadId => Assert.Equal(dispatcher.ThreadId, threadId));
    }

    private static RegionSelectionCoordinator CreateCoordinator(
        FakeRegionSelectionSurface surface)
    {
        var monitor = Monitor();
        return new RegionSelectionCoordinator(
            () => [monitor],
            _ => null!,
            (_, _, _) => surface);
    }

    private static RegionSelectionCoordinator CreateCoordinator(
        IReadOnlyList<DisplayMonitor> monitors,
        Queue<FakeRegionSelectionSurface> surfaces,
        Action<Action>? dispatch = null)
    {
        return new RegionSelectionCoordinator(
            () => monitors,
            _ => null!,
            (_, _, _) => surfaces.Dequeue(),
            dispatch);
    }

    private static DisplayMonitor Monitor() =>
        Monitor(@"\\.\DISPLAY1");

    private static DisplayMonitor Monitor(
        string deviceName,
        int left = 0) =>
        new(
            deviceName,
            new PixelRect(left, 0, 100, 100),
            new PixelRect(left, 0, 100, 100),
            true);

    private static CaptureFrame DesktopFrame() =>
        new(
            new PixelRect(0, 0, 100, 100),
            400,
            new byte[100 * 100 * 4]);

    private sealed class FakeRegionSelectionSurface : IRegionSelectionSurface
    {
        public event Action<PixelPoint>? DragStarted;
        public event Action<PixelPoint>? DragMoved;
        public event Action<PixelPoint>? DragCompleted;
        public event Action? CancelRequested;
        public event Action? ConfirmRequested;
        public event Action<int, int>? NudgeRequested;

        public TaskCompletionSource Shown { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PixelRect> Selections { get; } = [];
        public List<int> SurfaceThreadIds { get; } = [];
        public bool Hidden { get; private set; }
        public bool ClosedWithoutCancellation { get; private set; }
        public int ShowCount { get; private set; }
        public int CloseCount { get; private set; }
        public Exception? ShowException { get; init; }

        public void ShowSelection(PixelRect selection)
        {
            RecordThread();
            Selections.Add(selection);
        }
        public void Show()
        {
            RecordThread();
            if (ClosedWithoutCancellation)
                throw new InvalidOperationException("Cannot show a closed surface.");
            ShowCount++;
            if (ShowException is not null)
                throw ShowException;
            Shown.TrySetResult();
        }
        public void PlaceWindow()
        {
            RecordThread();
        }

        public void Activate()
        {
            RecordThread();
        }

        public void Focus()
        {
            RecordThread();
        }

        public void Hide()
        {
            RecordThread();
            Hidden = true;
        }
        public void CloseWithoutCancelling()
        {
            RecordThread();
            ClosedWithoutCancellation = true;
            CloseCount++;
        }

        public void RaiseDragStarted(PixelPoint point) => DragStarted?.Invoke(point);
        public void RaiseDragMoved(PixelPoint point) => DragMoved?.Invoke(point);
        public void RaiseDragCompleted(PixelPoint point) => DragCompleted?.Invoke(point);
        public void RaiseCancelRequested() => CancelRequested?.Invoke();
        public void RaiseConfirmRequested() => ConfirmRequested?.Invoke();
        public void RaiseNudgeRequested(int dx, int dy) => NudgeRequested?.Invoke(dx, dy);

        private void RecordThread() =>
            SurfaceThreadIds.Add(Environment.CurrentManagedThreadId);
    }

    private sealed class SingleThreadDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _work = [];
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;

        public SingleThreadDispatcher()
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                _ready.TrySetResult();
                foreach (var action in _work.GetConsumingEnumerable())
                    action();
            })
            {
                IsBackground = true,
                Name = "Region selection test dispatcher"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; private set; }

        public void Dispatch(Action action)
        {
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                action();
                return;
            }

            Exception? exception = null;
            using var completed = new ManualResetEventSlim();
            _work.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception current)
                {
                    exception = current;
                }
                finally
                {
                    completed.Set();
                }
            });
            completed.Wait();
            if (exception is not null)
                ExceptionDispatchInfo.Capture(exception).Throw();
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _work.Dispose();
        }
    }
}
