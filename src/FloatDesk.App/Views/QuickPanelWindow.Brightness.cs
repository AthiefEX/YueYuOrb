using System.Windows.Controls;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    public event Action? BrightnessRequested;
    public event Action? NightModeRequested;
    private Button? _nightButton;
    private void ApplyBrightnessWheel(AppSettings settings)
    {
        if (settings.Brightness.ShowBrightnessInWheel)
            AddAudioButton("亮度调节", "调节所有屏幕亮度", "\uE706", "MenuBlueBrush", () => BrightnessRequested?.Invoke());
        _nightButton = null;
        if (settings.Brightness.ShowNightModeInWheel)
        {
            AddAudioButton("黑夜模式", "开启或关闭屏幕降暗", "\uE708", "MenuVioletBrush", () => NightModeRequested?.Invoke());
            _nightButton = WheelCanvas.Children.OfType<Button>().Last();
        }
    }
    public void SetNightModeEnabled(bool enabled)
    {
        if (_nightButton is null) return;
        var grid = (Grid)_nightButton.Content;
        var label = (Border)grid.Children[1];
        ((TextBlock)label.Child).Text = enabled ? "黑夜模式 ✓" : "黑夜模式";
        System.Windows.Automation.AutomationProperties.SetName(_nightButton, enabled ? "黑夜模式，已开启" : "黑夜模式，已关闭");
    }
}
