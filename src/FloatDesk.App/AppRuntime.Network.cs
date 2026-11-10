using FloatDesk.Windows.Network;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private readonly SteamNetworkController _steamNetwork;

    private async void ToggleSteamNetwork()
    {
        if (_steamNetwork.IsBusy) return;
        try
        {
            if (_steamNetwork.IsRunning) await _steamNetwork.StopAsync();
            else await Task.Run(() => _steamNetwork.StartAsync(_settings.Current.SteamNetwork, _lifetime.Token));
            _tray?.ShowInfo("Steam 网络加速", _steamNetwork.Status);
        }
        catch (Exception exception) when (exception is System.IO.IOException or InvalidOperationException or System.ComponentModel.Win32Exception or System.UnauthorizedAccessException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Cryptography.CryptographicException)
        {
            _tray?.ShowWarning("Steam 网络加速", exception.Message);
        }
    }

    private async Task StopSteamNetworkForSettingsAsync()
    {
        try { await _steamNetwork.StopAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            await _logger.WriteAsync("network.restore.failed", new Dictionary<string, object?> { ["errorType"] = exception.GetType().Name });
            _tray?.ShowWarning("Steam 网络加速", "网络配置未完全恢复，请打开设置中的“恢复网络配置”。");
        }
    }
}
