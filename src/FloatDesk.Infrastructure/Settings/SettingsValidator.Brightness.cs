using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static partial class SettingsValidator
{
    private static void ValidateBrightness(AppSettings value, List<string> errors)
    {
        if (value.Brightness is not { } settings)
        {
            errors.Add("Brightness settings are required.");
            return;
        }
        if (settings.TargetPercent is < 0 or > 100) errors.Add("Brightness.TargetPercent must be 0-100.");
        if (settings.StepPercent is < 1 or > 100) errors.Add("Brightness.StepPercent must be 1-100.");
        if (settings.NightDimmingPercent is < 0 or > 90) errors.Add("Brightness.NightDimmingPercent must be 0-90.");
    }
}
