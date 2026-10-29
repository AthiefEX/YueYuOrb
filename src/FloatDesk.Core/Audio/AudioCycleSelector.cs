using FloatDesk.Core.Models;

namespace FloatDesk.Core.Audio;

public static class AudioCycleSelector
{
    public static string? ChooseNext(AudioCycleGroup group, AudioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(snapshot);
        var connected = snapshot.Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = group.DeviceIds.Where(connected.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            return null;
        var current = Array.IndexOf(candidates, snapshot.DefaultDeviceId);
        if (current < 0)
            return candidates[0];
        return candidates.Length == 1 ? null : candidates[(current + 1) % candidates.Length];
    }
}
