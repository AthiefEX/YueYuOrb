using System.Windows;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private readonly Dictionary<string, WindowPlacement> _popupPlacements = [];
    private PopupPlacementSaveQueue? _popupPositionSaves;
    private bool _popupShutdownInProgress;
    private void RememberPopupPosition(Window window, string id)
    {
        var placement = _popupPlacements.GetValueOrDefault(id) ?? _settings.Current.Windows.FirstOrDefault(p => p.Id == id);
        if (window is AudioControlWindow audio) audio.RememberedPlacement = placement;
        if (window is BrightnessControlWindow brightness) brightness.RememberedPlacement = placement;
        window.Closing += (_, _) =>
        {
            if (!window.IsLoaded) return;
            var saved = PopupWindowInteraction.CapturePlacement(window, id);
            _popupPlacements[id] = saved;
            _popupPositionSaves ??= new(SavePopupPositionAsync);
            _ = _popupPositionSaves.Enqueue(saved);
        };
    }
    private async Task SavePopupPositionAsync(WindowPlacement placement)
    {
        try
        {
            await _settings.UpdateAsync(current => current with
            { Windows = [.. current.Windows.Where(p => p.Id != placement.Id), placement] }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
    private void FlushPopupPositionSaves()
    {
        try { _popupPositionSaves?.Completion.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
    }
    private async Task<bool> PreparePopupShutdownAsync()
    {
        if (_popupShutdownInProgress) return false;
        _popupShutdownInProgress = true;
        _popupPositionSaves ??= new(SavePopupPositionAsync);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _popupPositionSaves.DrainAfterClosingAsync(() =>
            {
                _audioWindow?.Close();
                _brightnessWindow?.Close();
            }, timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            _popupShutdownInProgress = false;
            _tray?.ShowWarning("退出暂缓", "正在保存窗口位置，请稍后再次退出。");
            return false;
        }
    }
}
