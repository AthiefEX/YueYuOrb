using System.Windows;
using FloatDesk.App.Capture;
using FloatDesk.App.Views;
using FloatDesk.Core;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Input;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Coordination;

public sealed class CaptureCoordinator : IDisposable
{
    private readonly WindowCoordinator _windows;
    private readonly InputLockEngine _input;
    private readonly GdiScreenCapture _capture;
    private readonly Func<AppSettings> _settings;
    private readonly Func<nint> _lastExternalWindow;
    private readonly Action<bool> _activeChanged;
    private readonly Action<string, IReadOnlyDictionary<string, object?>?>? _diagnostics;
    private readonly CaptureSessionGate _sessionGate;
    private readonly RegionSelectionCoordinator _regionSelection;
    private readonly bool _ownsSessionGate;
    private readonly bool _ownsRegionSelection;

    public CaptureCoordinator(
        WindowCoordinator windows,
        InputLockEngine input,
        Func<AppSettings> settings,
        Func<nint> lastExternalWindow,
        Action<bool> activeChanged,
        Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics = null,
        CaptureSessionGate? sessionGate = null,
        RegionSelectionCoordinator? regionSelection = null)
    {
        _windows = windows;
        _input = input;
        _settings = settings;
        _lastExternalWindow = lastExternalWindow;
        _activeChanged = activeChanged;
        _diagnostics = diagnostics;
        _ownsSessionGate = sessionGate is null;
        _sessionGate = sessionGate ?? new CaptureSessionGate();
        _ownsRegionSelection = regionSelection is null;
        _regionSelection = regionSelection ?? new RegionSelectionCoordinator();
        _capture = new GdiScreenCapture();
    }

    internal RegionSelectionCoordinator RegionSelection => _regionSelection;

    public async Task<OperationResult> StartAsync(
        CaptureMode mode,
        CancellationToken cancellationToken)
    {
        CaptureSessionGate.Lease? session = null;
        var activeChanged = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session = _sessionGate.TryEnter();
            if (session is null)
                return OperationResult.Fail("CaptureBusy", "已有截图会话正在进行。");
            Log(
                "capture.start",
                new Dictionary<string, object?> { ["mode"] = mode.ToString() });
            _activeChanged(true);
            activeChanged = true;
            using var pause = _input.PauseForCapture();
            await using var visibility =
                await _windows.HideForCaptureAsync(cancellationToken);
            DesktopGeometry.FlushComposition();

            var settings = _settings();
            if (settings.Capture.DelaySeconds > 0 &&
                !await RunCountdownAsync(settings.Capture.DelaySeconds, cancellationToken))
            {
                return OperationResult.Fail("CaptureCancelled", "截图已取消。");
            }

            var target = mode switch
            {
                CaptureMode.ActiveWindow => DesktopGeometry.GetVisibleWindowBounds(
                    _lastExternalWindow()),
                CaptureMode.CurrentMonitor => DisplayTopology.FromPoint(
                    DesktopGeometry.GetCursorPosition())?.Bounds
                    ?? throw new InvalidOperationException("无法确定当前屏幕。"),
                _ => DesktopGeometry.GetVirtualDesktopBounds()
            };

            using var captured = await _capture.CaptureAsync(target, cancellationToken);
            Log("capture.frozen", new Dictionary<string, object?> { ["bounds"] = target.ToString() });
            PixelRect? selectedBounds = mode switch
            {
                CaptureMode.Region => await _regionSelection.SelectAsync(
                    captured,
                    _diagnostics,
                    cancellationToken),
                _ => target
            };
            if (selectedBounds is null)
                return OperationResult.Fail("CaptureCancelled", "截图已取消。");
            using var selected = mode == CaptureMode.Region
                ? CaptureExportService.Crop(captured, selectedBounds.Value)
                : Clone(captured);
            if (settings.Capture.OpenEditor)
            {
                var editor = new CaptureEditorWindow(selected, settings.Capture);
                editor.Show();
                await editor.Completed.WaitAsync(cancellationToken);
                return OperationResult.Ok();
            }

            var bitmap = CaptureExportService.Render(selected, []);
            var copy = await CaptureExportService.CopyAsync(bitmap, cancellationToken);
            OperationResult? save = null;
            if (settings.Capture.AutoSave)
            {
                var directory = settings.Capture.SaveDirectory ??
                                Path.Combine(
                                    Environment.GetFolderPath(
                                        Environment.SpecialFolder.MyPictures),
                                    ProductInfo.EnglishName);
                var path = CaptureExportService.CreateAutomaticPath(
                    directory,
                    settings.Capture.Format);
                save = await CaptureExportService.SaveAsync(
                    bitmap,
                    path,
                    settings.Capture.Format,
                    cancellationToken);
            }
            return copy.Success && (save?.Success ?? true)
                ? OperationResult.Ok("截图已复制。")
                : OperationResult.Fail(
                    "CaptureExportFailed",
                    $"{copy.Message} {save?.Message}".Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Fail("CaptureCancelled", "截图已取消。");
        }
        catch (Exception exception)
        {
            return OperationResult.Fail("CaptureFailed", exception.Message);
        }
        finally
        {
            if (activeChanged)
                _activeChanged(false);
            session?.Dispose();
        }
    }

    private static async Task<bool> RunCountdownAsync(
        int seconds,
        CancellationToken cancellationToken)
    {
        var countdown = new CountdownWindow();
        countdown.Show();
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            countdown.SetCountdown(remaining);
            var completed = await Task.WhenAny(
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken),
                countdown.Completed);
            if (completed == countdown.Completed)
            {
                var accepted = await countdown.Completed;
                countdown.Close();
                return accepted;
            }
        }
        countdown.Finish();
        countdown.Close();
        DesktopGeometry.FlushComposition();
        return true;
    }

    private void Log(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties) =>
        _diagnostics?.Invoke(eventName, properties);

    private static CaptureFrame Clone(CaptureFrame source)
    {
        var pixels = source.Bgra32.ToArray();
        return new CaptureFrame(source.Bounds, source.Stride, pixels);
    }

    public void Dispose()
    {
        _capture.Dispose();
        if (_ownsRegionSelection)
            _regionSelection.Dispose();
        if (_ownsSessionGate)
            _sessionGate.Dispose();
    }
}
