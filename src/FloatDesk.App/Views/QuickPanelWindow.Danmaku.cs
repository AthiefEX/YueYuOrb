using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    public event Action? DanmakuRequested;
    private void ApplyDanmakuWheel(AppSettings settings)
    {
        if (settings.Danmaku.ShowInWheel)
            AddAudioButton(settings.Danmaku.Enabled ? "通知弹幕 ✓" : "通知弹幕", "开启或关闭 Windows 系统通知弹幕", "\uE8F2", "MenuBlueBrush", () => DanmakuRequested?.Invoke());
    }
}
