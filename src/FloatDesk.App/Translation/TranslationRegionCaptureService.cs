using FloatDesk.App.Capture;
using FloatDesk.App.Coordination;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Translation;

public sealed record CapturedTranslationRegion(
    CaptureFrame Frame,
    PixelRect Bounds,
    TranslationCaptureRegion? Region = null);

public interface ITranslationSubtitleCapture
{
    Task<CapturedTranslationRegion?> CaptureRegionAsync(
        TranslationCaptureRegion region,
        CancellationToken cancellationToken);
}

public interface ITranslationRegionCapture : ITranslationSubtitleCapture
{
    Task<CapturedTranslationRegion?> CaptureSelectedAsync(
        CancellationToken cancellationToken);
}

public sealed class TranslationRegionCaptureService :
    ITranslationRegionCapture,
    IDisposable
{
    private readonly CaptureSessionGate _sessionGate;
    private readonly Func<IWindowCoordinator?> _windows;
    private readonly RegionSelectionCoordinator _regionSelection;
    private readonly IScreenCapture _capture;
    private readonly Func<PixelRect> _getVirtualDesktopBounds;
    private readonly Func<IReadOnlyList<DisplayMonitor>> _getMonitors;
    private readonly Action _flushComposition;
    private readonly Action<string, IReadOnlyDictionary<string, object?>?>?
        _diagnostics;
    private readonly bool _ownsCapture;

    public TranslationRegionCaptureService(
        CaptureSessionGate sessionGate,
        IWindowCoordinator windows,
        RegionSelectionCoordinator regionSelection,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics = null)
        : this(
            sessionGate,
            () => windows,
            regionSelection,
            new GdiScreenCapture(),
            DesktopGeometry.GetVirtualDesktopBounds,
            DisplayTopology.GetMonitors,
            DesktopGeometry.FlushComposition,
            diagnostics,
            ownsCapture: true)
    {
    }

    internal TranslationRegionCaptureService(
        CaptureSessionGate sessionGate,
        Func<IWindowCoordinator?> windows,
        RegionSelectionCoordinator regionSelection,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics = null)
        : this(
            sessionGate,
            windows,
            regionSelection,
            new GdiScreenCapture(),
            DesktopGeometry.GetVirtualDesktopBounds,
            DisplayTopology.GetMonitors,
            DesktopGeometry.FlushComposition,
            diagnostics,
            ownsCapture: true)
    {
    }

    internal TranslationRegionCaptureService(
        CaptureSessionGate sessionGate,
        Func<IWindowCoordinator?> windows,
        RegionSelectionCoordinator regionSelection,
        IScreenCapture capture,
        Func<PixelRect> getVirtualDesktopBounds,
        Func<IReadOnlyList<DisplayMonitor>> getMonitors,
        Action flushComposition,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics,
        bool ownsCapture)
    {
        _sessionGate = sessionGate ??
            throw new ArgumentNullException(nameof(sessionGate));
        _windows = windows ??
            throw new ArgumentNullException(nameof(windows));
        _regionSelection = regionSelection ??
            throw new ArgumentNullException(nameof(regionSelection));
        _capture = capture ??
            throw new ArgumentNullException(nameof(capture));
        _getVirtualDesktopBounds = getVirtualDesktopBounds ??
            throw new ArgumentNullException(nameof(getVirtualDesktopBounds));
        _getMonitors = getMonitors ??
            throw new ArgumentNullException(nameof(getMonitors));
        _flushComposition = flushComposition ??
            throw new ArgumentNullException(nameof(flushComposition));
        _diagnostics = diagnostics;
        _ownsCapture = ownsCapture;
    }

    public async Task<CapturedTranslationRegion?> CaptureSelectedAsync(
        CancellationToken cancellationToken)
    {
        using var session = await _sessionGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(true);
        var windows = _windows() ??
            throw new InvalidOperationException(
                "The window coordinator is not ready.");
        await using var visibility =
            await windows.HideForCaptureAsync(cancellationToken)
                .ConfigureAwait(true);
        _flushComposition();

        var virtualBounds = _getVirtualDesktopBounds();
        using var desktop = await _capture.CaptureAsync(
            virtualBounds,
            cancellationToken).ConfigureAwait(true);
        var selectedBounds = await _regionSelection.SelectAsync(
            desktop,
            _diagnostics,
            cancellationToken).ConfigureAwait(true);
        if (selectedBounds is null)
            return null;

        var frame = CaptureExportService.Crop(
            desktop,
            selectedBounds.Value);
        var monitor = SelectMonitorForBounds(
            _getMonitors(),
            selectedBounds.Value);
        return new CapturedTranslationRegion(
            frame,
            selectedBounds.Value,
            monitor is null
                ? null
                : ToRelativeRegion(monitor, selectedBounds.Value));
    }

    public async Task<CapturedTranslationRegion?> CaptureRegionAsync(
        TranslationCaptureRegion region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(region);
        ValidateRegion(region);
        using var session = await _sessionGate
            .EnterAsync(cancellationToken)
            .ConfigureAwait(true);
        var monitor = _getMonitors().FirstOrDefault(item =>
            string.Equals(
                item.DeviceName,
                region.MonitorId,
                StringComparison.OrdinalIgnoreCase));
        if (monitor is null)
        {
            throw new InvalidOperationException(
                "字幕区域所在的显示器不可用，请重新选择区域。");
        }

        var bounds = ToPixelBounds(monitor, region);
        _flushComposition();
        var frame = await _capture.CaptureAsync(
            bounds,
            cancellationToken).ConfigureAwait(true);
        return new CapturedTranslationRegion(
            frame,
            bounds,
            region);
    }

    public void Dispose()
    {
        if (_ownsCapture && _capture is IDisposable disposable)
            disposable.Dispose();
    }

    private static void ValidateRegion(TranslationCaptureRegion region)
    {
        if (string.IsNullOrWhiteSpace(region.MonitorId) ||
            !double.IsFinite(region.RelativeX) ||
            !double.IsFinite(region.RelativeY) ||
            !double.IsFinite(region.RelativeWidth) ||
            !double.IsFinite(region.RelativeHeight) ||
            region.RelativeX is < 0 or > 1 ||
            region.RelativeY is < 0 or > 1 ||
            region.RelativeWidth is <= 0 or > 1 ||
            region.RelativeHeight is <= 0 or > 1 ||
            region.RelativeX + region.RelativeWidth > 1.0000001 ||
            region.RelativeY + region.RelativeHeight > 1.0000001)
        {
            throw new ArgumentException(
                "The subtitle capture region is invalid.",
                nameof(region));
        }
    }

    private static PixelRect ToPixelBounds(
        DisplayMonitor monitor,
        TranslationCaptureRegion region)
    {
        var monitorBounds = monitor.Bounds;
        var left = Clamp(
            monitorBounds.Left +
            (int)Math.Round(region.RelativeX * monitorBounds.Width),
            monitorBounds.Left,
            monitorBounds.Right - 1);
        var top = Clamp(
            monitorBounds.Top +
            (int)Math.Round(region.RelativeY * monitorBounds.Height),
            monitorBounds.Top,
            monitorBounds.Bottom - 1);
        var right = Clamp(
            left +
            Math.Max(
                1,
                (int)Math.Round(
                    region.RelativeWidth * monitorBounds.Width)),
            left + 1,
            monitorBounds.Right);
        var bottom = Clamp(
            top +
            Math.Max(
                1,
                (int)Math.Round(
                    region.RelativeHeight * monitorBounds.Height)),
            top + 1,
            monitorBounds.Bottom);
        return new PixelRect(
            left,
            top,
            right - left,
            bottom - top);
    }

    private static DisplayMonitor? SelectMonitorForBounds(
        IReadOnlyList<DisplayMonitor> monitors,
        PixelRect bounds)
    {
        DisplayMonitor? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var intersection = Intersection(monitor.Bounds, bounds);
            var area = (long)intersection.Width * intersection.Height;
            if (area > bestArea)
            {
                best = monitor;
                bestArea = area;
            }
        }
        return best;
    }

    private static TranslationCaptureRegion ToRelativeRegion(
        DisplayMonitor monitor,
        PixelRect bounds)
    {
        var visible = Intersection(monitor.Bounds, bounds);
        return new TranslationCaptureRegion(
            monitor.DeviceName,
            Math.Clamp(
                (visible.Left - monitor.Bounds.Left) /
                (double)monitor.Bounds.Width,
                0,
                1),
            Math.Clamp(
                (visible.Top - monitor.Bounds.Top) /
                (double)monitor.Bounds.Height,
                0,
                1),
            Math.Clamp(
                visible.Width / (double)monitor.Bounds.Width,
                double.Epsilon,
                1),
            Math.Clamp(
                visible.Height / (double)monitor.Bounds.Height,
                double.Epsilon,
                1));
    }

    private static PixelRect Intersection(
        PixelRect left,
        PixelRect right)
    {
        var x1 = Math.Max(left.Left, right.Left);
        var y1 = Math.Max(left.Top, right.Top);
        var x2 = Math.Min(left.Right, right.Right);
        var y2 = Math.Min(left.Bottom, right.Bottom);
        return x2 <= x1 || y2 <= y1
            ? default
            : new PixelRect(x1, y1, x2 - x1, y2 - y1);
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum
            ? minimum
            : Math.Clamp(value, minimum, maximum);
}
