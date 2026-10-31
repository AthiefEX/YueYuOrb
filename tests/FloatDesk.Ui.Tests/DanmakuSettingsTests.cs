using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DanmakuSettingsTests
{
    [Fact]
    public void LiveSourcesPreserveEditsAndSelectionMergesWithConcurrentChanges()
    {
        var initial = AppSettings.CreateDefault() with { Danmaku = new() { SelectedApplications = [new("saved", "已保存应用")] } };
        var vm = SettingsViewModel.From(initial);
        vm.RefreshDanmakuApplications([new("a", "应用A"), new("b", "应用B")]);
        vm.DanmakuApplications.Single(a => a.Id == "a").IsSelected = true;
        vm.DanmakuApplications.Single(a => a.Id == "saved").IsSelected = false;
        vm.RefreshDanmakuApplications([new("a", "应用A新名称"), new("c", "应用C")]);
        Assert.True(vm.DanmakuApplications.Single(a => a.Id == "a").IsSelected);
        var latest = initial with { Danmaku = initial.Danmaku with { SelectedApplications = [new("saved", "已保存应用"), new("external", "并发选择")] } };
        var result = vm.BuildCandidate(latest);
        Assert.Equal(["a", "external"], result.Danmaku.SelectedApplications.Select(a => a.Id).OrderBy(id => id));
        Assert.DoesNotContain(result.Danmaku.SelectedApplications, a => a.Id == "b" || a.Id == "c");
    }
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
