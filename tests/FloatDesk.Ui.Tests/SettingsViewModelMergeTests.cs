using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

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
    public void UntouchedBrowserThemePreservesConcurrentChange()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        var latest = original with
        {
            BrowserTheme = BrowserThemeMode.Dark
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(BrowserThemeMode.Dark, candidate.BrowserTheme);
    }

    [Fact]
    public void EditedBrowserThemeWinsOverConcurrentChange()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        viewModel.BrowserThemeIndex = (int)BrowserThemeMode.Light;
        var latest = original with
        {
            BrowserTheme = BrowserThemeMode.Dark
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(BrowserThemeMode.Light, candidate.BrowserTheme);
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

    [Fact]
    public void UntouchedTranslationPreservesConcurrentProviderChange()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        var latest = original with
        {
            Translation = original.Translation with
            {
                Provider = TranslationProviderKind.DeepL
            }
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(TranslationProviderKind.DeepL, candidate.Translation.Provider);
    }

    [Fact]
    public void EditedTranslationTargetWinsOverConcurrentValue()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        viewModel.TranslationTargetLanguageIndex =
            SettingsViewModel.TranslationTargetLanguages.ToList()
                .IndexOf(TranslationLanguages.English);
        var latest = original with
        {
            Translation = original.Translation with
            {
                TargetLanguage = TranslationLanguages.Japanese
            }
        };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(TranslationLanguages.English, candidate.Translation.TargetLanguage);
    }

    [Fact]
    public void TranslationHotkeysAndWheelTogglesMerge()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        viewModel.TranslationSelectionHotkey = "Ctrl+Alt+Y";
        viewModel.TranslationRegionHotkey = "Ctrl+Shift+U";
        viewModel.SubtitlesHotkey = "Ctrl+Alt+K";
        viewModel.ShowTranslateSubtitlesInWheel = false;

        var candidate = viewModel.BuildCandidate(original);

        Assert.Equal(
            0x59u,
            candidate.Hotkeys[HotkeyAction.TranslateSelection].VirtualKey);
        Assert.Equal(
            0x55u,
            candidate.Hotkeys[HotkeyAction.TranslateRegion].VirtualKey);
        Assert.Equal(
            ShortcutModifiers.Control | ShortcutModifiers.Shift,
            candidate.Hotkeys[HotkeyAction.TranslateRegion].Modifiers);
        Assert.Equal(
            0x4Bu,
            candidate.Hotkeys[HotkeyAction.ToggleSubtitles].VirtualKey);
        Assert.DoesNotContain(
            WheelActionKind.TranslateSubtitles,
            candidate.WheelActions);
    }
}
