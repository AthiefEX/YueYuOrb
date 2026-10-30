using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private int _brightnessTarget = 50;
    [ObservableProperty] private int _brightnessStep = 5;
    [ObservableProperty] private int _nightDimming = 40;
    [ObservableProperty] private bool _showBrightnessInWheel = true;
    [ObservableProperty] private bool _showNightModeInWheel = true;
    public ObservableCollection<BrightnessKeyViewModel> BrightnessKeys { get; } = [];

    private void LoadBrightness(AppSettings settings)
    {
        BrightnessTarget = settings.Brightness.TargetPercent;
        BrightnessStep = settings.Brightness.StepPercent;
        NightDimming = settings.Brightness.NightDimmingPercent;
        ShowBrightnessInWheel = settings.Brightness.ShowBrightnessInWheel;
        ShowNightModeInWheel = settings.Brightness.ShowNightModeInWheel;
        foreach (var (action, label) in new[] {
            (HotkeyAction.BrightnessPanel, "打开亮度面板"), (HotkeyAction.BrightnessPreset, "设为指定亮度"),
            (HotkeyAction.BrightnessUp, "提高亮度"), (HotkeyAction.BrightnessDown, "降低亮度"),
            (HotkeyAction.ToggleNightMode, "切换黑夜模式"), (HotkeyAction.RecoverBrightness, "恢复可见亮度") })
            BrightnessKeys.Add(new(action, label, FormatHotkey(settings.Hotkeys.GetValueOrDefault(action))));
    }

    private BrightnessSettings MergeBrightness(BrightnessSettings latest)
    {
        var baseline = _baseline.Brightness;
        return latest with {
            TargetPercent = BrightnessTarget != baseline.TargetPercent ? BrightnessTarget : latest.TargetPercent,
            StepPercent = BrightnessStep != baseline.StepPercent ? BrightnessStep : latest.StepPercent,
            NightDimmingPercent = NightDimming != baseline.NightDimmingPercent ? NightDimming : latest.NightDimmingPercent,
            ShowBrightnessInWheel = ShowBrightnessInWheel != baseline.ShowBrightnessInWheel ? ShowBrightnessInWheel : latest.ShowBrightnessInWheel,
            ShowNightModeInWheel = ShowNightModeInWheel != baseline.ShowNightModeInWheel ? ShowNightModeInWheel : latest.ShowNightModeInWheel };
    }

    private void MergeBrightnessKeys(Dictionary<HotkeyAction, HotkeyChord> keys)
    {
        foreach (var key in BrightnessKeys)
        {
            if (key.Text == FormatHotkey(_baseline.Hotkeys.GetValueOrDefault(key.Action))) continue;
            if (string.IsNullOrWhiteSpace(key.Text)) keys.Remove(key.Action);
            else if (TryParseHotkey(key.Text, out var chord)) keys[key.Action] = chord;
            else throw new InvalidOperationException($"{key.Label}快捷键格式无效，请使用 Ctrl+Alt+F8 等格式。");
        }
    }
}

public partial class BrightnessKeyViewModel(HotkeyAction action, string label, string text) : ObservableObject
{
    public HotkeyAction Action { get; } = action;
    public string Label { get; } = label;
    [ObservableProperty] private string _text = text;
}
