using FloatDesk.App.ViewModels;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class AudioSettingsViewModelTests
{
    [Fact]
    public void UntouchedAudioRetainsExternalGroupsAndFlag()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.OrbSizeDip = 72;
        var latest = initial with { AudioGroups = [new(Guid.NewGuid(), "外部", ["x"])], ShowAudioInWheel = false };
        var candidate = vm.BuildCandidate(latest);
        Assert.Same(latest.AudioGroups, candidate.AudioGroups);
        Assert.False(candidate.ShowAudioInWheel);
        Assert.Equal(72, candidate.Orb.SizeDip);
    }

    [Fact]
    public void EditingAudioMergesAgainstLatestWithoutAliasingOriginalOrCandidate()
    {
        var sourceIds = new List<string> { "offline", "speakers" };
        var initial = AppSettings.CreateDefault() with { AudioGroups = [new(Guid.NewGuid(), "原始", sourceIds)] };
        var vm = SettingsViewModel.From(initial);
        vm.AudioGroups[0].Name = "新名称";
        vm.AudioGroups[0].ShowInWheel = false;
        vm.AudioGroups[0].Devices.Move(1, 0);
        var latest = initial with { BrowserTopmost = true, ShowAudioInWheel = false, Orb = initial.Orb with { SizeDip = 88 } };
        var candidate = vm.BuildCandidate(latest);
        Assert.Equal("新名称", candidate.AudioGroups[0].Name);
        Assert.False(candidate.AudioGroups[0].ShowInWheel);
        Assert.Equal(["speakers", "offline"], candidate.AudioGroups[0].DeviceIds);
        Assert.False(candidate.ShowAudioInWheel);
        Assert.True(candidate.BrowserTopmost);
        Assert.Equal(88, candidate.Orb.SizeDip);
        Assert.Equal("原始", initial.AudioGroups[0].Name);
        Assert.Equal(["offline", "speakers"], sourceIds);
        vm.AudioGroups[0].Devices.Clear();
        Assert.Equal(2, candidate.AudioGroups[0].DeviceIds.Count);
    }

    [Fact]
    public void WheelFlagEditPreservesExternalAudioGroups()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.ShowAudioInWheel = false;
        var latest = initial with { AudioGroups = [new(Guid.NewGuid(), "外部", [])] };
        var candidate = vm.BuildCandidate(latest);
        Assert.False(candidate.ShowAudioInWheel);
        Assert.Same(latest.AudioGroups, candidate.AudioGroups);
    }

    [Fact]
    public async Task RefreshRetainsUnavailableMembersAndNeverChangesAudio()
    {
        var initial = AppSettings.CreateDefault() with { AudioGroups = [new(Guid.NewGuid(), "组", ["offline", "speakers"])] };
        var vm = SettingsViewModel.From(initial);
        var service = new ReadOnlyAudioService();
        await vm.RefreshAudioDevicesAsync(service);
        Assert.Equal(0, service.Mutations);
        Assert.Contains("不可用", vm.AudioGroups[0].Devices[0].DisplayName);
        Assert.Equal("扬声器", vm.AudioGroups[0].Devices[1].DisplayName);
        Assert.Equal("speakers", Assert.Single(vm.AvailableAudioDevices).Id);
        Assert.Same(initial.AudioGroups, vm.BuildCandidate(initial).AudioGroups);
        service.Devices = [];
        await vm.RefreshAudioDevicesAsync(service);
        Assert.Contains("不可用", vm.AudioGroups[0].Devices[1].DisplayName);
        Assert.Equal(["offline", "speakers"], vm.BuildCandidate(initial).AudioGroups[0].DeviceIds);
        Assert.Equal(0, service.Mutations);
    }

    [Fact]
    public async Task MissingServiceRetainsSavedIdsAndAllowsEditing()
    {
        var initial = AppSettings.CreateDefault() with { AudioGroups = [new(Guid.NewGuid(), "组", ["saved-id"])] };
        var vm = SettingsViewModel.From(initial);
        await vm.RefreshAudioDevicesAsync(null);
        Assert.False(vm.IsRefreshingAudioDevices);
        Assert.Contains("未连接", vm.AudioDeviceStatus);
        Assert.Contains("saved-id", vm.AudioGroups[0].Devices[0].DisplayName);
        vm.AudioGroups[0].Name = "新组名";
        Assert.Equal("saved-id", Assert.Single(vm.BuildCandidate(initial).AudioGroups[0].DeviceIds));
    }

    [Fact]
    public void RevertedEditsDoNotOverwriteExternalAudioChanges()
    {
        var initial = AppSettings.CreateDefault() with { AudioGroups = [new(Guid.NewGuid(), "组", ["a", "b"])] };
        var vm = SettingsViewModel.From(initial);
        vm.AudioGroups[0].Devices.Move(0, 1);
        vm.AudioGroups[0].Devices.Move(0, 1);
        vm.AudioGroups[0].Name = "临时";
        vm.AudioGroups[0].Name = "组";
        var latest = initial with { AudioGroups = [new(Guid.NewGuid(), "外部", [])] };
        Assert.Same(latest.AudioGroups, vm.BuildCandidate(latest).AudioGroups);
    }

    [Fact]
    public void GroupAndMembershipCommandsPreserveOrderAndPreventDuplicates()
    {
        var vm = SettingsViewModel.From(AppSettings.CreateDefault());
        vm.AddAudioGroup();
        var first = vm.SelectedAudioGroup!;
        vm.AddAudioGroup();
        var second = vm.SelectedAudioGroup!;
        vm.MoveAudioGroup(-1);
        Assert.Same(second, vm.AudioGroups[0]);
        vm.SelectedAvailableAudioDevice = new AudioDeviceChoice("a", "A");
        vm.AddAudioDevice();
        vm.AddAudioDevice();
        Assert.Single(second.Devices);
        vm.SelectedAvailableAudioDevice = new AudioDeviceChoice("b", "B");
        vm.AddAudioDevice();
        vm.MoveAudioDevice(-1);
        Assert.Equal(["b", "a"], second.Devices.Select(d => d.Id));
        vm.RemoveAudioDevice();
        Assert.Equal("a", Assert.Single(second.Devices).Id);
        vm.RemoveAudioGroup();
        Assert.Same(first, Assert.Single(vm.AudioGroups));
        for (var i = 0; i < 20; i++) vm.AddAudioGroup();
        Assert.Equal(16, vm.AudioGroups.Count);
    }

    private sealed class ReadOnlyAudioService : IAudioService
    {
        public int Mutations { get; private set; }
        public IReadOnlyList<AudioDevice> Devices { get; set; } = [new("speakers", "扬声器")];
        public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new AudioSnapshot(Devices, "speakers", .5, false));
        public Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken) { Mutations++; throw new InvalidOperationException("Editing must not switch audio."); }
        public Task SetVolumeAsync(string deviceId, double volume, CancellationToken cancellationToken) { Mutations++; throw new InvalidOperationException("Editing must not change volume."); }
        public Task SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken) { Mutations++; throw new InvalidOperationException("Editing must not mute."); }
    }
}
