using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsViewModelMergeTests
{
    [Fact]
    public void UneditedSettingsDoNotOverwriteConcurrentChanges()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        var latest = original with
        {
            BrowserTopmost = true,
            Capture = original.Capture with { AutoSave = true },
            Windows =
            [
                new WindowPlacement("orb", @"\\.\DISPLAY2", 0.8, 0.2, 64, 64)
            ]
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.True(candidate.BrowserTopmost);
        Assert.True(candidate.Capture.AutoSave);
        Assert.Equal(latest.Windows, candidate.Windows);
    }

    [Fact]
    public void EditedFieldMergesIntoLatestVersionOfItsSettingsGroup()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        viewModel.OrbSizeDip = 64;
        var latest = original with
        {
            Orb = original.Orb with { ShowClock = true }
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(64, candidate.Orb.SizeDip);
        Assert.True(candidate.Orb.ShowClock);
    }

    [Fact]
    public void InvalidHotkeyPreventsApply()
    {
        var viewModel = SettingsViewModel.From(AppSettings.CreateDefault());
        viewModel.PanelHotkey = "Ctrl+?";

        var valid = viewModel.TryBuildCandidate(
            AppSettings.CreateDefault(),
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains("快捷键", error, StringComparison.Ordinal);
    }
}
