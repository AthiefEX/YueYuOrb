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
        catch (InvalidOperationException exception)
        {
            errors.Add(exception.Message);
        }

        if (value.DesktopSessions.Environments.Any(environment =>
                !Enum.IsDefined(environment.Backend)))
        {
            errors.Add("Desktop session backend is invalid.");
        }

        if (value.DesktopSessions.Tasks.Any(task =>
                !Enum.IsDefined(task.Trigger.Kind) ||
                task.Steps.Count > 200))
        {
            errors.Add("Desktop session tasks contain invalid limits.");
        }
    }
}
