using System.Windows;
using FloatDesk.Core.Contracts;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private readonly IAudioService? _audioService;

    private Task RefreshAudioDevicesAsync() => _viewModel.RefreshAudioDevicesAsync(_audioService);
    private async void OnAudioDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded) await RefreshAudioDevicesAsync();
    }
    private async void OnRefreshAudioDevicesClick(object sender, RoutedEventArgs e) => await RefreshAudioDevicesAsync();
    private void OnAddAudioGroupClick(object sender, RoutedEventArgs e) => _viewModel.AddAudioGroup();
    private void OnRemoveAudioGroupClick(object sender, RoutedEventArgs e) => _viewModel.RemoveAudioGroup();
    private void OnAudioGroupUpClick(object sender, RoutedEventArgs e) => _viewModel.MoveAudioGroup(-1);
    private void OnAudioGroupDownClick(object sender, RoutedEventArgs e) => _viewModel.MoveAudioGroup(1);
    private void OnAddAudioDeviceClick(object sender, RoutedEventArgs e) => _viewModel.AddAudioDevice();
    private void OnRemoveAudioDeviceClick(object sender, RoutedEventArgs e) => _viewModel.RemoveAudioDevice();
    private void OnAudioDeviceUpClick(object sender, RoutedEventArgs e) => _viewModel.MoveAudioDevice(-1);
    private void OnAudioDeviceDownClick(object sender, RoutedEventArgs e) => _viewModel.MoveAudioDevice(1);
}
