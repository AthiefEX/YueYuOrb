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

    private static RegionSelectionCoordinator CreateCoordinator(
        FakeRegionSelectionSurface surface)
    {
        var monitor = Monitor();
        return new RegionSelectionCoordinator(
            () => [monitor],
            _ => null!,
            (_, _, _) => surface);
    }

    private static DisplayMonitor Monitor() =>
        new(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 100, 100),
            new PixelRect(0, 0, 100, 100),
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
        public bool Hidden { get; private set; }
        public bool ClosedWithoutCancellation { get; private set; }
        public int ShowCount { get; private set; }
        public int CloseCount { get; private set; }

        public void ShowSelection(PixelRect selection) => Selections.Add(selection);
        public void Show()
        {
            if (ClosedWithoutCancellation)
                throw new InvalidOperationException("Cannot show a closed surface.");
            ShowCount++;
            Shown.TrySetResult();
        }
        public void PlaceWindow()
        {
        }

        public void Activate()
        {
        }

        public void Focus()
        {
        }

        public void Hide() => Hidden = true;
        public void CloseWithoutCancelling()
        {
            ClosedWithoutCancellation = true;
            CloseCount++;
        }

        public void RaiseDragStarted(PixelPoint point) => DragStarted?.Invoke(point);
        public void RaiseDragMoved(PixelPoint point) => DragMoved?.Invoke(point);
        public void RaiseDragCompleted(PixelPoint point) => DragCompleted?.Invoke(point);
        public void RaiseCancelRequested() => CancelRequested?.Invoke();
        public void RaiseConfirmRequested() => ConfirmRequested?.Invoke();
        public void RaiseNudgeRequested(int dx, int dy) => NudgeRequested?.Invoke(dx, dy);
    }
}
