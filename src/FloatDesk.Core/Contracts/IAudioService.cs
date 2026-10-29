using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IAudioService
{
    Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken);
    Task SetVolumeAsync(string deviceId, double volume, CancellationToken cancellationToken);
    Task SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken);
}
