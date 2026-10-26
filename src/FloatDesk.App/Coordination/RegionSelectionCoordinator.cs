using System.Windows.Media.Imaging;
using FloatDesk.App.Capture;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Coordination;

public sealed class RegionSelectionCoordinator
{
    private readonly Func<IReadOnlyList<DisplayMonitor>> _getMonitors;
    private readonly Func<CaptureFrame, BitmapSource> _bitmapFactory;
    private readonly Func<
        DisplayMonitor,
        PixelRect,
        BitmapSource,
        IRegionSelectionSurface> _surfaceFactory;

    public RegionSelectionCoordinator()
        : this(
            DisplayTopology.GetMonitors,
            CaptureExportService.ToBitmap,
            static (monitor, frozenBounds, bitmap) =>
                new CaptureOverlaySurface(new CaptureOverlayWindow(
                    monitor,
                    frozenBounds,
                    bitmap)))
    {
    }

    internal RegionSelectionCoordinator(
        Func<IReadOnlyList<DisplayMonitor>> getMonitors,
        Func<CaptureFrame, BitmapSource> bitmapFactory,
        Func<DisplayMonitor, PixelRect, BitmapSource, IRegionSelectionSurface> surfaceFactory)
    {
        _getMonitors = getMonitors;
        _bitmapFactory = bitmapFactory;
        _surfaceFactory = surfaceFactory;
    }

    public async Task<PixelRect?> SelectAsync(
        CaptureFrame desktop,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desktop);
        if (cancellationToken.IsCancellationRequested)
            return null;

        var bitmap = _bitmapFactory(desktop);
        var monitors = _getMonitors();
        diagnostics?.Invoke(
            "capture.region.overlays",
            new Dictionary<string, object?>
            {
                ["count"] = monitors.Count,
                ["frozenBounds"] = desktop.Bounds.ToString()
            });
        var surfaces = monitors
            .Select(monitor => _surfaceFactory(
                monitor,
                desktop.Bounds,
                bitmap))
            .ToArray();
        if (surfaces.Length == 0)
            return null;

        var completion = new TaskCompletionSource<PixelRect?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PixelPoint? anchor = null;
        var selection = new PixelRect(0, 0, 0, 0);

        void UpdateSelection(PixelPoint current)
        {
            if (anchor is null)
                return;
            selection = SelectionGeometry.FromPoints(anchor.Value, current);
            foreach (var surface in surfaces)
                surface.ShowSelection(selection);
        }

        void Complete(PixelRect? result)
        {
            if (!completion.TrySetResult(result))
                return;
            foreach (var surface in surfaces)
            {
                surface.Hide();
                surface.CloseWithoutCancelling();
            }
        }

        foreach (var surface in surfaces)
        {
            surface.DragStarted += point =>
            {
                anchor = point;
                diagnostics?.Invoke(
                    "capture.region.drag.started",
                    new Dictionary<string, object?>
                    {
                        ["anchor"] = anchor.Value.ToString()
                    });
                UpdateSelection(anchor.Value);
            };
            surface.DragMoved += UpdateSelection;
            surface.DragCompleted += point =>
            {
                UpdateSelection(point);
                diagnostics?.Invoke(
                    "capture.region.drag.completed",
                    new Dictionary<string, object?>
                    {
                        ["selection"] = selection.ToString()
                    });
            };
            surface.CancelRequested += () =>
            {
                diagnostics?.Invoke("capture.region.cancelled", null);
                Complete(null);
            };
            surface.ConfirmRequested += () =>
            {
                diagnostics?.Invoke(
                    "capture.region.confirmed",
                    new Dictionary<string, object?>
                    {
                        ["selection"] = selection.ToString(),
                        ["usable"] = SelectionGeometry.IsUsable(selection)
                    });
                if (SelectionGeometry.IsUsable(selection))
                    Complete(selection);
            };
            surface.NudgeRequested += (dx, dy) =>
            {
                if (!SelectionGeometry.IsUsable(selection))
                    return;
                selection = ApplyNudge(
                    selection,
                    desktop.Bounds,
                    dx,
                    dy);
                foreach (var current in surfaces)
                    current.ShowSelection(selection);
            };
        }

        using var registration = cancellationToken.Register(() => Complete(null));
        foreach (var surface in surfaces)
        {
            surface.Show();
            surface.PlaceWindow();
        }
        if (!completion.Task.IsCompleted)
        {
            surfaces[0].Activate();
            surfaces[0].Focus();
        }
        return await completion.Task.ConfigureAwait(true);
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
