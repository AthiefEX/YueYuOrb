using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private bool _browserAlwaysOnTop = true;
    [ObservableProperty] private bool _browserOpenOnStartup;
    [ObservableProperty] private string _browserStartupUrl = "";
    [ObservableProperty] private double _browserOpacityPercent = 100;
    [ObservableProperty] private string _browserInteractionHotkey = "Ctrl+Alt+B";
    [ObservableProperty] private bool _customBackgroundEnabled;
    [ObservableProperty] private double _customBackgroundOpacityPercent = 25;

    private void LoadFloatingBrowser(AppSettings settings)
    {
        BrowserAlwaysOnTop = settings.FloatingBrowser.AlwaysOnTop;
        BrowserOpenOnStartup = settings.FloatingBrowser.OpenOnStartup;
        BrowserStartupUrl = settings.FloatingBrowser.StartupUrl;
        BrowserOpacityPercent = settings.FloatingBrowser.Opacity * 100;
        BrowserInteractionHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.ToggleBrowserInteraction, AppSettings.CreateDefault().Hotkeys[HotkeyAction.ToggleBrowserInteraction]));
        CustomBackgroundEnabled = settings.CustomBackground.Enabled;
        CustomBackgroundOpacityPercent = settings.CustomBackground.Opacity * 100;
    }

    private FloatingBrowserSettings MergeFloatingBrowser(FloatingBrowserSettings current) => current with
    {
        AlwaysOnTop = BrowserAlwaysOnTop != _baseline.FloatingBrowser.AlwaysOnTop ? BrowserAlwaysOnTop : current.AlwaysOnTop,
        OpenOnStartup = BrowserOpenOnStartup != _baseline.FloatingBrowser.OpenOnStartup ? BrowserOpenOnStartup : current.OpenOnStartup,
        StartupUrl = BrowserStartupUrl != _baseline.FloatingBrowser.StartupUrl ? BrowserStartupUrl.Trim() : current.StartupUrl,
        Opacity = BrowserOpacityPercent != _baseline.FloatingBrowser.Opacity * 100 ? BrowserOpacityPercent / 100 : current.Opacity
    };

    private CustomBackgroundSettings MergeCustomBackground(CustomBackgroundSettings current) => current with
    {
        Enabled = CustomBackgroundEnabled != _baseline.CustomBackground.Enabled ? CustomBackgroundEnabled : current.Enabled,
        Opacity = CustomBackgroundOpacityPercent != _baseline.CustomBackground.Opacity * 100 ? CustomBackgroundOpacityPercent / 100 : current.Opacity
    };
}
