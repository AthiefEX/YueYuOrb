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
        var completion = new TaskCompletionSource<PixelRect?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new object();
        var surfaces = new List<IRegionSelectionSurface>();
        var shown = new HashSet<IRegionSelectionSurface>();
        var closeStarted = false;
        PixelPoint? anchor = null;
        var selection = new PixelRect(0, 0, 0, 0);

        static void CloseSurface(
            IRegionSelectionSurface surface,
            bool wasShown)
        {
            try
            {
                if (wasShown)
                    surface.Hide();
            }
            catch (Exception)
            {
                // Closing is best effort; one broken overlay must not strand
                // the remaining selection windows.
            }
            try
            {
                surface.CloseWithoutCancelling();
            }
            catch (Exception)
            {
            }
        }

        void Complete(PixelRect? result)
        {
            (IRegionSelectionSurface Surface, bool WasShown)[] snapshot;
            lock (sync)
            {
                if (!completion.TrySetResult(result))
                    return;
                closeStarted = true;
                snapshot = surfaces
                    .Select(surface => (
                        Surface: surface,
                        WasShown: shown.Contains(surface)))
                    .ToArray();
            }
            foreach (var (surface, wasShown) in snapshot)
                CloseSurface(surface, wasShown);
        }

        void UpdateSelection(PixelPoint current)
        {
            lock (sync)
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
        }

        using var registration =
            cancellationToken.Register(() => Complete(null));
        if (completion.Task.IsCompleted)
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

        foreach (var monitor in monitors)
        {
            if (completion.Task.IsCompleted)
                break;

            var surface = _surfaceFactory(
                monitor,
                desktop.Bounds,
                bitmap);
            var closeImmediately = false;
            lock (sync)
            {
                surfaces.Add(surface);
                closeImmediately =
                    closeStarted || completion.Task.IsCompleted;
            }
            if (closeImmediately)
            {
                CloseSurface(surface, false);
                break;
            }
        }

        if (completion.Task.IsCompleted || surfaces.Count == 0)
        {
            Complete(null);
            return null;
        }

        lock (sync)
        {
            foreach (var surface in surfaces)
            {
                surface.DragStarted += point =>
                {
                    lock (sync)
                    {
                        if (closeStarted)
                            return;
                        anchor = point;
                    }
                    diagnostics?.Invoke(
                        "capture.region.drag.started",
                        new Dictionary<string, object?>
                        {
                            ["anchor"] = point.ToString()
                        });
                    UpdateSelection(point);
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
                    lock (sync)
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
                    }
                };
            }
        }

        lock (sync)
        {
            foreach (var surface in surfaces)
            {
                if (closeStarted || completion.Task.IsCompleted)
                    break;
                shown.Add(surface);
                surface.Show();
                if (closeStarted || completion.Task.IsCompleted)
                    break;
                surface.PlaceWindow();
            }
            if (!closeStarted && !completion.Task.IsCompleted)
            {
                surfaces[0].Activate();
                surfaces[0].Focus();
            }
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
