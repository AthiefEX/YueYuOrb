using System.Windows;
using System.Windows.Media.Imaging;
using FloatDesk.App.Capture;
using FloatDesk.App.Views;
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
    private readonly SemaphoreSlim _session = new(1, 1);

    public CaptureCoordinator(
        WindowCoordinator windows,
        InputLockEngine input,
        Func<AppSettings> settings,
        Func<nint> lastExternalWindow,
        Action<bool> activeChanged)
    {
        _windows = windows;
        _input = input;
        _settings = settings;
        _lastExternalWindow = lastExternalWindow;
        _activeChanged = activeChanged;
        _capture = new GdiScreenCapture();
    }

    public async Task<OperationResult> StartAsync(
        CaptureMode mode,
        CancellationToken cancellationToken)
    {
        if (!await _session.WaitAsync(0, cancellationToken))
            return OperationResult.Fail("CaptureBusy", "已有截图会话正在进行。");
        try
        {
            _activeChanged(true);
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
            PixelRect? selectedBounds = mode switch
            {
                CaptureMode.Region => await SelectRegionAsync(
                    captured,
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
                                    "FloatDesk");
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
            _capture.Dispose();
            _activeChanged(false);
            _session.Release();
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

    private static async Task<PixelRect?> SelectRegionAsync(
        CaptureFrame frozenDesktop,
        CancellationToken cancellationToken)
    {
        var bitmap = CaptureExportService.ToBitmap(frozenDesktop);
        var overlays = DisplayTopology.GetMonitors()
            .Select(monitor => new CaptureOverlayWindow(
                monitor,
                frozenDesktop.Bounds,
                bitmap))
            .ToArray();
        var completion = new TaskCompletionSource<PixelRect?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        PixelPoint? anchor = null;
        PixelRect selection = new(0, 0, 0, 0);

        void UpdateSelection(PixelPoint current)
        {
            if (anchor is null)
                return;
            selection = SelectionGeometry.FromPoints(anchor.Value, current);
            foreach (var overlay in overlays)
                overlay.ShowSelection(selection);
        }

        void Complete(PixelRect? result)
        {
            if (!completion.TrySetResult(result))
                return;
            foreach (var overlay in overlays)
            {
                overlay.Hide();
                overlay.CloseWithoutCancelling();
            }
        }

        foreach (var overlay in overlays)
        {
            overlay.DragStarted += () =>
            {
                anchor = DesktopGeometry.GetCursorPosition();
                UpdateSelection(anchor.Value);
            };
            overlay.DragMoved += () =>
                UpdateSelection(DesktopGeometry.GetCursorPosition());
            overlay.DragCompleted += () =>
                UpdateSelection(DesktopGeometry.GetCursorPosition());
            overlay.CancelRequested += () => Complete(null);
            overlay.ConfirmRequested += () =>
            {
                if (SelectionGeometry.IsUsable(selection))
                    Complete(selection);
            };
            overlay.NudgeRequested += (dx, dy) =>
            {
                if (!SelectionGeometry.IsUsable(selection))
                    return;
                var left = Math.Clamp(
                    selection.Left + dx,
                    frozenDesktop.Bounds.Left,
                    frozenDesktop.Bounds.Right - selection.Width);
                var top = Math.Clamp(
                    selection.Top + dy,
                    frozenDesktop.Bounds.Top,
                    frozenDesktop.Bounds.Bottom - selection.Height);
                selection = new PixelRect(
                    left,
                    top,
                    selection.Width,
                    selection.Height);
                foreach (var current in overlays)
                    current.ShowSelection(selection);
            };
        }

        foreach (var overlay in overlays)
        {
            overlay.Show();
            overlay.PlaceWindow();
        }
        overlays[0].Activate();
        overlays[0].Focus();

        using var registration = cancellationToken.Register(() => Complete(null));
        return await completion.Task;
    }

    private static CaptureFrame Clone(CaptureFrame source)
    {
        var pixels = source.Bgra32.ToArray();
        return new CaptureFrame(source.Bounds, source.Stride, pixels);
    }

    public void Dispose()
    {
        _capture.Dispose();
        _session.Dispose();
    }
}
