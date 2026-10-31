using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    public event Action? DanmakuRequested;
    private void ApplyDanmakuWheel(AppSettings settings)
    {
        if (settings.Danmaku.ShowInWheel)
            AddAudioButton(settings.Danmaku.Enabled ? "消息弹幕 ✓" : "消息弹幕", "开启或关闭通知弹幕", "\uE8F2", "MenuBlueBrush", () => DanmakuRequested?.Invoke());
    }
}
