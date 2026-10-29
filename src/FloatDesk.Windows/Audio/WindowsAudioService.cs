using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Audio;

internal interface IAudioBackend
{
    AudioSnapshot GetSnapshot(CancellationToken token);
    void SetDefaultDevice(string deviceId, CancellationToken token);
    void SetVolume(string deviceId, double volume, CancellationToken token);
    void SetMute(string deviceId, bool muted, CancellationToken token);
}

/// <summary>Serializes Core Audio calls on a dedicated MTA thread. Volume is normalized to 0..1.</summary>
public sealed class WindowsAudioService : IAudioService, IDisposable
{
    private readonly IAudioBackend backend;
    private readonly AudioComDispatcher dispatcher;

    public WindowsAudioService() : this(new CoreAudioBackend()) { }

    internal WindowsAudioService(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        this.backend = backend;
        dispatcher = new AudioComDispatcher();
    }

    public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        dispatcher.Invoke(() => backend.GetSnapshot(cancellationToken), cancellationToken);

    public Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        return dispatcher.Invoke(() => { backend.SetDefaultDevice(deviceId, cancellationToken); return true; }, cancellationToken);
    }

    public Task SetVolumeAsync(string deviceId, double volume, CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        if (!double.IsFinite(volume) || volume < 0 || volume > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be finite and between 0 and 1.");
        return dispatcher.Invoke(() => { backend.SetVolume(deviceId, volume, cancellationToken); return true; }, cancellationToken);
    }

    public Task SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken)
    {
        ValidateDeviceId(deviceId);
        return dispatcher.Invoke(() => { backend.SetMute(deviceId, muted, cancellationToken); return true; }, cancellationToken);
    }

    private static void ValidateDeviceId(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (deviceId.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Endpoint IDs cannot contain a null character.", nameof(deviceId));
    }

    /// <summary>Stops accepting work and waits for accepted operations and COM cleanup to finish.</summary>
    public void Dispose() => dispatcher.Dispose();
}

