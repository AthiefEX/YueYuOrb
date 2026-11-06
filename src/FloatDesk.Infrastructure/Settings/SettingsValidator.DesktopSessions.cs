using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static partial class SettingsValidator
{
    private static void ValidateDesktopSessions(
        AppSettings value,
        List<string> errors)
    {
        try
        {
            DesktopSessionSettingsValidator.Validate(value.DesktopSessions);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException)
        {
            errors.Add(exception.Message);
        }
    }
}
