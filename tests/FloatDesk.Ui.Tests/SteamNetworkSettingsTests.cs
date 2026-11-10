using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class SteamNetworkSettingsTests
{
    [Fact]
    public void DefaultsAreOptInAndApplyPreservesOtherLivePreferences()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        Assert.False(vm.NetworkSystemPac);
        Assert.False(vm.NetworkInWheel);
        Assert.True(vm.NetworkNoSniCompatibility);
        Assert.True(vm.NetworkCompatibility);
        vm.NetworkCompatibility = false;
        vm.NetworkInWheel = true;
        vm.NetworkCommunity = false;
        var latest = initial with { Orb = initial.Orb with { SizeDip = 72 } };
        var candidate = vm.BuildCandidate(latest);
        Assert.True(candidate.SteamNetwork.ShowInWheel);
        Assert.False(candidate.SteamNetwork.UseHostsCompatibility);
        Assert.False(candidate.SteamNetwork.Community);
        Assert.Equal(72, candidate.Orb.SizeDip);
    }

    [Fact]
    public void UneditedFieldsPreserveLiveNetworkUpdates()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.NetworkInWheel = true;
        var latest = initial with { SteamNetwork = initial.SteamNetwork with { UseSystemPac = true, Store = false, UseHostsCompatibility = false } };
        var candidate = vm.BuildCandidate(latest);
        Assert.True(candidate.SteamNetwork.UseSystemPac);
        Assert.False(candidate.SteamNetwork.UseHostsCompatibility);
        Assert.False(candidate.SteamNetwork.Store);
        Assert.True(candidate.SteamNetwork.ShowInWheel);
    }

    [Fact]
    public void NoSniCompatibilityDefaultsOnAndUneditedLiveValueIsPreserved()
    {
        var initial = AppSettings.CreateDefault();
        Assert.True(initial.SteamNetwork.UseNoSniCompatibility);
        var vm = SettingsViewModel.From(initial);
        var latest = initial with { SteamNetwork = initial.SteamNetwork with { UseNoSniCompatibility = false } };

        // Unedited: the live value wins.
        Assert.False(vm.BuildCandidate(latest).SteamNetwork.UseNoSniCompatibility);

        // Edited: the explicit choice wins over the live value.
        var live = initial with { SteamNetwork = initial.SteamNetwork with { UseNoSniCompatibility = true } };
        vm.NetworkNoSniCompatibility = false;
        Assert.False(vm.BuildCandidate(live).SteamNetwork.UseNoSniCompatibility);
    }
}
