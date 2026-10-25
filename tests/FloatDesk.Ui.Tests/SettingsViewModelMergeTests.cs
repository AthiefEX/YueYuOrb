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
    public void WheelPlacementChangeIsPersisted()
    {
        var launcher = new LauncherItem(
            Guid.NewGuid(),
            "编辑器",
            LauncherKind.Exe,
            @"C:\Apps\editor.exe",
            [],
            null,
            0);
        var original = AppSettings.CreateDefault() with
        {
            Launchers = [launcher]
        };
        var viewModel = SettingsViewModel.From(original);

        viewModel.Launchers[0] = launcher with { ShowInWheel = true };
        var candidate = viewModel.BuildCandidate(original);

        Assert.True(candidate.Launchers.Single().ShowInWheel);
        Assert.Equal(launcher.Id, candidate.Launchers.Single().Id);
    }

    [Fact]
    public void BuiltInWheelActionsCanBeAddedAndRemoved()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);

        viewModel.ShowNotesInWheel = false;
        viewModel.ShowInputLockInWheel = false;
        var candidate = viewModel.BuildCandidate(original);

        Assert.DoesNotContain(
            WheelActionKind.Notes,
            candidate.WheelActions);
        Assert.DoesNotContain(
            WheelActionKind.InputLock,
            candidate.WheelActions);
        Assert.Contains(
            WheelActionKind.RegionCapture,
            candidate.WheelActions);

        var restored = SettingsViewModel.From(candidate);
        restored.ShowNotesInWheel = true;
        var updated = restored.BuildCandidate(candidate);

        Assert.Contains(WheelActionKind.Notes, updated.WheelActions);
        Assert.DoesNotContain(
            WheelActionKind.InputLock,
            updated.WheelActions);
    }

    [Fact]
    public void UntouchedWheelActionsPreserveConcurrentChanges()
    {
        var original = AppSettings.CreateDefault() with
        {
            WheelActions =
            [
                WheelActionKind.Settings,
                WheelActionKind.Notes,
                WheelActionKind.InputLock
            ]
        };
        var viewModel = SettingsViewModel.From(original);
        var latest = original with
        {
            Orb = original.Orb with { ShowClock = true },
            WheelActions =
            [
                WheelActionKind.InputLock,
                WheelActionKind.Settings
            ]
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(latest.WheelActions, candidate.WheelActions);
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
