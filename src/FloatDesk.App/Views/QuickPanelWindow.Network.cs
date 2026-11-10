using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    public event Action? NetworkRequested;
    private void ApplyNetworkWheel(AppSettings settings)
    {
        if (settings.SteamNetwork.ShowInWheel)
            AddAudioButton("Steam 网络加速", "启动或停止 Steam 定向加速", "\uE774", "MenuBlueBrush", () => NetworkRequested?.Invoke());
    }
}
