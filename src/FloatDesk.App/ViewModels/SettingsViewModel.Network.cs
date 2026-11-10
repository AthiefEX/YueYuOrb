using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private bool _networkCompatibility = true;
    [ObservableProperty] private bool _networkStore = true;
    [ObservableProperty] private bool _networkCommunity = true;
    [ObservableProperty] private bool _networkContent = true;
    [ObservableProperty] private bool _networkHttpsDns = true;
    [ObservableProperty] private bool _networkNoSniCompatibility = true;
    [ObservableProperty] private bool _networkSystemPac;
    [ObservableProperty] private bool _networkInWheel;

    private void LoadNetwork(SteamNetworkSettings settings)
    {
        NetworkCompatibility = settings.UseHostsCompatibility;
        NetworkStore = settings.Store;
        NetworkCommunity = settings.Community;
        NetworkContent = settings.Content;
        NetworkHttpsDns = settings.UseHttpsDns;
        NetworkNoSniCompatibility = settings.UseNoSniCompatibility;
        NetworkSystemPac = settings.UseSystemPac;
        NetworkInWheel = settings.ShowInWheel;
    }

    public SteamNetworkSettings BuildNetwork(SteamNetworkSettings latest)
    {
        var baseline = _baseline.SteamNetwork;
        return latest with
        {
            UseHostsCompatibility = NetworkCompatibility != baseline.UseHostsCompatibility ? NetworkCompatibility : latest.UseHostsCompatibility,
            Store = NetworkStore != baseline.Store ? NetworkStore : latest.Store,
            Community = NetworkCommunity != baseline.Community ? NetworkCommunity : latest.Community,
            Content = NetworkContent != baseline.Content ? NetworkContent : latest.Content,
            UseHttpsDns = NetworkHttpsDns != baseline.UseHttpsDns ? NetworkHttpsDns : latest.UseHttpsDns,
            UseNoSniCompatibility = NetworkNoSniCompatibility != baseline.UseNoSniCompatibility ? NetworkNoSniCompatibility : latest.UseNoSniCompatibility,
            UseSystemPac = NetworkSystemPac != baseline.UseSystemPac ? NetworkSystemPac : latest.UseSystemPac,
            ShowInWheel = NetworkInWheel != baseline.ShowInWheel ? NetworkInWheel : latest.ShowInWheel
        };
    }
}
