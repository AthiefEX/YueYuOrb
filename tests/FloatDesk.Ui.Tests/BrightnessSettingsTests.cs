using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class BrightnessSettingsTests
{
    [Theory]
    [InlineData("+", 0x6Bu)]
    [InlineData("-", 0x6Du)]
    [InlineData("Add", 0x6Bu)]
    [InlineData("Subtract", 0x6Du)]
    [InlineData("小键盘+", 0x6Bu)]
    [InlineData("小键盘-", 0x6Du)]
    [InlineData("NumPad+", 0x6Bu)]
    [InlineData("NumPad-", 0x6Du)]
    public void NumpadBrightnessKeysDoNotRequireModifiers(string text, uint virtualKey)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = text;
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
        var chord = candidate.Hotkeys[HotkeyAction.BrightnessUp];
        Assert.Equal(virtualKey, chord.VirtualKey);
        Assert.Equal(ShortcutModifiers.None, chord.Modifiers);
        var reloaded = SettingsViewModel.From(candidate);
        Assert.True(reloaded.TryBuildCandidate(candidate, out var roundTrip, out error), error);
        Assert.Equal(chord, roundTrip.Hotkeys[HotkeyAction.BrightnessUp]);
    }

    [Fact]
    public void GeneralHotkeysAlsoAllowSingleKeys()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.PanelHotkey = "Add";
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
        Assert.Equal(new HotkeyChord(ShortcutModifiers.None, 0x6B), candidate.Hotkeys[HotkeyAction.Panel]);
    }

    [Theory]
    [InlineData("A", 0x41u)]
    [InlineData("7", 0x37u)]
    [InlineData("f8", 0x77u)]
    [InlineData("Up", 0x26u)]
    [InlineData("Enter", 0x0Du)]
    [InlineData("Tab", 0x09u)]
    [InlineData("Space", 0x20u)]
    [InlineData("NumPad7", 0x67u)]
    [InlineData("Multiply", 0x6Au)]
    [InlineData("Divide", 0x6Fu)]
    [InlineData("Decimal", 0x6Eu)]
    [InlineData("CapsLock", 0x14u)]
    [InlineData("PrintScreen", 0x2Cu)]
    [InlineData("VolumeUp", 0xAFu)]
    [InlineData("OemSemicolon", 0xBAu)]
    public void SingleStandardKeyboardKeysWorkAcrossGeneralAndBrightnessSettings(string text, uint virtualKey)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.PanelHotkey = text;
        vm.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = text;
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
        Assert.Equal(new HotkeyChord(ShortcutModifiers.None, virtualKey), candidate.Hotkeys[HotkeyAction.Panel]);
        Assert.Equal(candidate.Hotkeys[HotkeyAction.Panel], candidate.Hotkeys[HotkeyAction.BrightnessUp]);
        var reloaded = SettingsViewModel.From(candidate);
        Assert.True(reloaded.TryBuildCandidate(candidate, out var roundTrip, out error), error);
        Assert.Equal(candidate.Hotkeys[HotkeyAction.Panel], roundTrip.Hotkeys[HotkeyAction.Panel]);
    }

    [Theory]
    [InlineData("Ctrl+Alt+Up", 0x26u)]
    [InlineData("Ctrl+Alt+↑", 0x26u)]
    [InlineData("Ctrl+Alt+Down", 0x28u)]
    [InlineData("Ctrl+Alt+↓", 0x28u)]
    [InlineData("ctrl+alt+f8", 0x77u)]
    [InlineData("Ctrl+Alt+PageUp", 0x21u)]
    [InlineData("Ctrl+Alt+PageDown", 0x22u)]
    [InlineData("Ctrl+Alt++", 0xBBu)]
    [InlineData("Ctrl+Alt+-", 0xBDu)]
    [InlineData("Ctrl+Alt+Add", 0x6Bu)]
    [InlineData("Ctrl+Alt+Subtract", 0x6Du)]
    [InlineData("Ctrl+Alt+0x26", 0x26u)]
    public void CommonBrightnessKeysSaveAndRoundTrip(string text, uint virtualKey)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = text;
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
        var chord = candidate.Hotkeys[HotkeyAction.BrightnessUp];
        Assert.Equal(virtualKey, chord.VirtualKey);
        Assert.Equal(ShortcutModifiers.Control | ShortcutModifiers.Alt, chord.Modifiers);
        var reloaded = SettingsViewModel.From(candidate);
        Assert.True(reloaded.TryBuildCandidate(candidate, out var roundTrip, out error), error);
        Assert.Equal(chord, roundTrip.Hotkeys[HotkeyAction.BrightnessUp]);
    }

    [Theory]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F8+Up")]
    [InlineData("Ctrl++F8")]
    [InlineData("Ctrl+A,B")]
    [InlineData("Ctrl+48")]
    public void InvalidBrightnessKeyReportsTheOffendingCombination(string text)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.BrightnessKeys.Single(k => k.Action == HotkeyAction.BrightnessUp).Text = text;
        Assert.False(vm.TryBuildCandidate(initial, out _, out var error));
        Assert.Contains(text, error);
        Assert.Contains("提高亮度", error);
    }

    [Fact]
    public void StandardKeyboardNamesIncludingModifierKeysRoundTrip()
    {
        var initial = AppSettings.CreateDefault();
        foreach (var name in Enum.GetNames<System.Windows.Forms.Keys>())
        {
            var code = (uint)Enum.Parse<System.Windows.Forms.Keys>(name);
            if (code is < 0x08 or > 0xFE) continue;
            var vm = SettingsViewModel.From(initial);
            vm.PanelHotkey = name;
            Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), name + ": " + error);
            Assert.Equal(code, candidate.Hotkeys[HotkeyAction.Panel].VirtualKey);
            var reloaded = SettingsViewModel.From(candidate);
            Assert.True(reloaded.TryBuildCandidate(candidate, out var roundTrip, out error), name + ": " + error);
            Assert.Equal(candidate.Hotkeys[HotkeyAction.Panel], roundTrip.Hotkeys[HotkeyAction.Panel]);
        }
    }

    [Theory]
    [InlineData("Ctrl+ShiftKey")]
    [InlineData("Shift+ControlKey")]
    [InlineData("Ctrl+Menu")]
    [InlineData("Ctrl+LWin")]
    public void ModifierKeysUsedAsMainKeysRoundTripInCombinations(string text)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.PanelHotkey = text;
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
        var reloaded = SettingsViewModel.From(candidate);
        Assert.True(reloaded.TryBuildCandidate(candidate, out var roundTrip, out error), error);
        Assert.Equal(candidate.Hotkeys[HotkeyAction.Panel], roundTrip.Hotkeys[HotkeyAction.Panel]);
    }

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
