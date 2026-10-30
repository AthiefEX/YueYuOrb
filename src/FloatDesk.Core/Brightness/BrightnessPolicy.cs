using FloatDesk.Core.Models;

namespace FloatDesk.Core.Brightness;

public static class BrightnessPolicy
{
    public static IReadOnlyDictionary<string, int> CreateTargets(BrightnessSnapshot snapshot, int value, bool relative)
        => snapshot.Devices.Where(d => d.Percent.HasValue && d.UnavailableReason is null)
            .DistinctBy(d => d.Id).ToDictionary(d => d.Id,
                d => (int)Math.Clamp(relative ? (long)d.Percent!.Value + value : value, 0, 100));
}
