using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    private AudioCycleGroup[] _audioBaseline = [];
    [ObservableProperty] private bool _showAudioInWheel = true;
    [ObservableProperty] private AudioGroupViewModel? _selectedAudioGroup;
    [ObservableProperty] private AudioDeviceChoice? _selectedAvailableAudioDevice;
    [ObservableProperty] private AudioDeviceChoice? _selectedAudioGroupDevice;
    [ObservableProperty] private string _audioDeviceStatus = "点击刷新以获取当前播放设备。";
    [ObservableProperty] private bool _isRefreshingAudioDevices;
    public ObservableCollection<AudioGroupViewModel> AudioGroups { get; } = [];
    public ObservableCollection<AudioDeviceChoice> AvailableAudioDevices { get; } = [];

    private void LoadAudio(AppSettings settings)
    {
        ShowAudioInWheel = settings.ShowAudioInWheel;
        _audioBaseline = settings.AudioGroups.Select(g => g with { DeviceIds = g.DeviceIds.ToArray() }).ToArray();
        foreach (var group in _audioBaseline)
            AudioGroups.Add(new AudioGroupViewModel(group));
        SelectedAudioGroup = AudioGroups.FirstOrDefault();
    }

    private IReadOnlyList<AudioCycleGroup> MergeAudioGroups(IReadOnlyList<AudioCycleGroup> latest)
    {
        var edited = AudioGroups.Select(g => g.Snapshot()).ToArray();
        var unchanged = edited.Length == _audioBaseline.Length && edited.Zip(_audioBaseline).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name &&
            pair.First.ShowInWheel == pair.Second.ShowInWheel &&
            pair.First.DeviceIds.SequenceEqual(pair.Second.DeviceIds, StringComparer.Ordinal));
        return unchanged ? latest : edited;
    }

    partial void OnSelectedAudioGroupChanged(AudioGroupViewModel? value) => SelectedAudioGroupDevice = null;

    public void AddAudioGroup()
    {
        if (AudioGroups.Count >= 16) return;
        var group = new AudioGroupViewModel(new AudioCycleGroup(Guid.NewGuid(), $"循环组 {AudioGroups.Count + 1}", []));
        AudioGroups.Add(group);
        SelectedAudioGroup = group;
    }

    public void RemoveAudioGroup()
    {
        if (SelectedAudioGroup is not { } group) return;
        var index = AudioGroups.IndexOf(group);
        AudioGroups.Remove(group);
        SelectedAudioGroup = AudioGroups.ElementAtOrDefault(Math.Min(index, AudioGroups.Count - 1));
    }

    public void MoveAudioGroup(int direction)
    {
        if (SelectedAudioGroup is not { } group) return;
        Move(AudioGroups, group, direction);
        SelectedAudioGroup = group;
    }

    public void AddAudioDevice()
    {
        if (SelectedAudioGroup is not { } group || SelectedAvailableAudioDevice is not { } device ||
            group.Devices.Any(d => d.Id == device.Id)) return;
        var member = new AudioDeviceChoice(device.Id, device.DisplayName);
        group.Devices.Add(member);
        SelectedAudioGroupDevice = member;
    }

    public void RemoveAudioDevice()
    {
        if (SelectedAudioGroup is not { } group || SelectedAudioGroupDevice is not { } device) return;
        var index = group.Devices.IndexOf(device);
        group.Devices.Remove(device);
        SelectedAudioGroupDevice = group.Devices.ElementAtOrDefault(Math.Min(index, group.Devices.Count - 1));
    }

    public void MoveAudioDevice(int direction)
    {
        if (SelectedAudioGroup is not { } group || SelectedAudioGroupDevice is not { } device) return;
        Move(group.Devices, device, direction);
        SelectedAudioGroupDevice = device;
    }

    private static void Move<T>(ObservableCollection<T> items, T item, int direction)
    {
        var index = items.IndexOf(item);
        var target = index + direction;
        if (index >= 0 && target >= 0 && target < items.Count) items.Move(index, target);
    }

    public async Task RefreshAudioDevicesAsync(IAudioService? audioService, CancellationToken cancellationToken = default)
    {
        if (IsRefreshingAudioDevices) return;
        if (audioService is null)
        {
            AudioDeviceStatus = "音频服务未连接；仍可编辑和保留已保存的设备。";
            return;
        }
        IsRefreshingAudioDevices = true;
        try
        {
            var snapshot = await audioService.GetSnapshotAsync(cancellationToken);
            var devices = snapshot.Devices.DistinctBy(d => d.Id).ToArray();
            var selectedId = SelectedAvailableAudioDevice?.Id;
            AvailableAudioDevices.Clear();
            foreach (var device in devices)
                AvailableAudioDevices.Add(new AudioDeviceChoice(device.Id, device.Name));
            SelectedAvailableAudioDevice = AvailableAudioDevices.FirstOrDefault(d => d.Id == selectedId)
                ?? AvailableAudioDevices.FirstOrDefault();
            foreach (var group in AudioGroups)
                foreach (var member in group.Devices)
                    member.DisplayName = devices.FirstOrDefault(d => d.Id == member.Id)?.Name
                        ?? AudioDeviceChoice.UnavailableName(member.Id);
            AudioDeviceStatus = devices.Length == 0 ? "未找到可用播放设备；已保存的设备会保留。" : $"找到 {devices.Length} 个播放设备。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            AudioDeviceStatus = "无法刷新播放设备，请重试。已保存的设备会保留。";
        }
        finally { IsRefreshingAudioDevices = false; }
    }
}

public partial class AudioGroupViewModel : ObservableObject
{
    public Guid Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _showInWheel;
    public ObservableCollection<AudioDeviceChoice> Devices { get; } = [];

    public AudioGroupViewModel(AudioCycleGroup group)
    {
        Id = group.Id;
        _name = group.Name;
        _showInWheel = group.ShowInWheel;
        foreach (var id in group.DeviceIds)
            Devices.Add(new AudioDeviceChoice(id, AudioDeviceChoice.UnavailableName(id)));
    }

    public AudioCycleGroup Snapshot() => new(Id, Name, Devices.Select(d => d.Id).ToArray(), ShowInWheel);
}

public partial class AudioDeviceChoice(string id, string displayName) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _displayName = displayName;
    internal static string UnavailableName(string id) => $"不可用 · {id}";
}
