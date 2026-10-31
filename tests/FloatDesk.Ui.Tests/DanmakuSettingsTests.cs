using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DanmakuSettingsTests
{
    [Fact]
    public void UserEditsMergeWithoutOverwritingConcurrentPositionOrOtherSettings()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.DanmakuFontSize = 32;
        vm.DanmakuTitleFilter = "测试群";
        vm.DanmakuToggleHotkey = "Ctrl+Alt+F9";
        var latest = initial with { Danmaku = initial.Danmaku with { RelativeX = .2 }, ShowLauncherDock = false };
        var result = vm.BuildCandidate(latest);
        Assert.Equal(32, result.Danmaku.FontSize);
        Assert.Equal("测试群", result.Danmaku.TitleFilter);
        Assert.Equal(.2, result.Danmaku.RelativeX);
        Assert.False(result.ShowLauncherDock);
        Assert.True(result.Hotkeys.ContainsKey(HotkeyAction.ToggleDanmaku));
        var reopened = SettingsViewModel.From(result);
        reopened.DanmakuToggleHotkey = "";
        Assert.False(reopened.BuildCandidate(result).Hotkeys.ContainsKey(HotkeyAction.ToggleDanmaku));
    }
}
