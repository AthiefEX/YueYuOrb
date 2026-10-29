using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static partial class SettingsValidator
{
    private static void ValidateAudio(AppSettings value, List<string> errors)
    {
        if (value.AudioGroups is null)
        {
            errors.Add("AudioGroups cannot be null.");
            return;
        }
        if (value.AudioGroups.Count > 16)
            errors.Add("AudioGroups may contain at most 16 groups.");
        var ids = new HashSet<Guid>();
        foreach (var group in value.AudioGroups)
        {
            if (group is null)
            {
                errors.Add("AudioGroups cannot contain null groups.");
                continue;
            }
            if (group.Id == Guid.Empty || !ids.Add(group.Id))
                errors.Add("AudioGroups must have unique nonempty IDs.");
            if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 80)
                errors.Add("AudioGroups names must contain 1-80 characters.");
            if (group.DeviceIds is null ||
                group.DeviceIds.Any(string.IsNullOrWhiteSpace) ||
                group.DeviceIds.Distinct(StringComparer.Ordinal).Count() != group.DeviceIds.Count)
                errors.Add("AudioGroups device IDs cannot be blank or duplicated within a group.");
        }
    }
}
