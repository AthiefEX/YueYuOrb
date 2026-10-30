using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class BrightnessSettingsTests
{
    [Fact]
    public void EditsMergeWithLatestAndOptionalHotkeysCanBeRemoved()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.BrightnessTarget = 30;
        vm.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = "Ctrl+Alt+F8";
        var latest = initial with { Brightness = initial.Brightness with { StepPercent = 10 } };
        var result = vm.BuildCandidate(latest);
        Assert.Equal(30, result.Brightness.TargetPercent);
        Assert.Equal(10, result.Brightness.StepPercent);
        Assert.Equal(0x77u, result.Hotkeys[HotkeyAction.BrightnessUp].VirtualKey);
        var edit = SettingsViewModel.From(result);
        edit.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = "";
        Assert.DoesNotContain(HotkeyAction.BrightnessUp, edit.BuildCandidate(result).Hotkeys.Keys);
    }
}
