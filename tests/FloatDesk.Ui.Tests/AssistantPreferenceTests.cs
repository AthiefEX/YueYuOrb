using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantPreferenceTests
{
    [Fact]
    public void AppearanceAndNamesSaveWithoutOverwritingLiveConnectionProfiles()
    {
        var initial = AppSettings.CreateDefault(); var vm = SettingsViewModel.From(initial);
        vm.AssistantWindowTitle = "我的助手"; vm.AssistantDisplayName = "小月"; vm.AssistantUserDisplayName = "主人"; vm.AssistantAlwaysOnTop = false;
        var profile = new FloatDesk.Core.Assistant.AssistantApiProfile(Guid.NewGuid(), "live", "https://live.test/v1", "live-model", true);
        var latest = initial with { Assistant = initial.Assistant with { Profiles = [profile], ActiveProfileId = profile.Id, SearchProfileId = profile.Id, SearchEnabled = true } };
        var saved = vm.BuildCandidate(latest).Assistant;
        Assert.Equal(profile, Assert.Single(saved.Profiles)); Assert.Equal(profile.Id, saved.ActiveProfileId); Assert.True(saved.SearchEnabled);
        Assert.Equal("我的助手", saved.WindowTitle); Assert.Equal("小月", saved.DisplayName); Assert.Equal("主人", saved.UserDisplayName); Assert.False(saved.Window.AlwaysOnTop);
    }

    [Fact]
    public void SavingAssistantFieldsKeepsLiveWindowPositionAndAssetSelection()
    {
        var initial = AppSettings.CreateDefault(); var vm = SettingsViewModel.From(initial);
        Assert.False(vm.AssistantEnabled); vm.AssistantModel = "chosen-model"; vm.AssistantOpacityPercent = 60;
        var asset = Guid.NewGuid(); var bounds = new BrowserWindowBounds(12, 34, 680, 720);
        var latest = initial with { Assistant = initial.Assistant with { EnabledAssetIds = [asset], Window = initial.Assistant.Window with { Bounds = bounds } } };
        var saved = vm.BuildCandidate(latest);
        Assert.Equal("chosen-model", saved.Assistant.Model); Assert.Equal(.6, saved.Assistant.Window.Opacity);
        Assert.Equal(bounds, saved.Assistant.Window.Bounds); Assert.Equal(asset, Assert.Single(saved.Assistant.EnabledAssetIds));
    }
}
