using FloatDesk.App.Capture;
using FloatDesk.App.Coordination;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;

namespace FloatDesk.App.Translation;

public sealed record CapturedTranslationRegion(
    CaptureFrame Frame,
    PixelRect Bounds);

public interface ITranslationRegionCapture
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
        return new CapturedTranslationRegion(
            frame,
            selectedBounds.Value);
    }

    public void Dispose()
    {
        if (_ownsCapture && _capture is IDisposable disposable)
            disposable.Dispose();
    }
}
