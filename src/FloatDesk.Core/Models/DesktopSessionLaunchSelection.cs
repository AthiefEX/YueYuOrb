namespace FloatDesk.Core.Models;

public static class DesktopSessionLaunchSelection
{
    public static IReadOnlyList<EnvironmentProfile> GetCandidates(DesktopSessionSettings settings) =>
        settings.Environments.Where(environment => DesktopSessionModePolicy.IsEnabled(settings, environment.Backend)).ToArray();
}
