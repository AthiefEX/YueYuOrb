using FloatDesk.Windows.Audio;

namespace FloatDesk.Windows.Tests;

public sealed class AudioNativeSmokeTests
{
    // READ ONLY: do not add SetDefaultDevice, SetVolume or SetMute calls to native tests.
    [Fact]
    [Trait("Category", "NativeReadOnly")]
    public async Task NativeSnapshotReadsFriendlyNamesAndDefaultVolumeWithoutChangingAudio()
    {
        using var service = new WindowsAudioService();
        var snapshot = await service.GetSnapshotAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.InRange(snapshot.Volume, 0, 1);
        Assert.All(snapshot.Devices, device =>
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        });
        Assert.Equal(snapshot.Devices.Count, snapshot.Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count());
        if (snapshot.DefaultDeviceId is null)
        {
            Assert.Equal(0, snapshot.Volume);
            Assert.False(snapshot.Muted);
        }
        else
            Assert.False(string.IsNullOrWhiteSpace(snapshot.DefaultDeviceId));
    }
}
