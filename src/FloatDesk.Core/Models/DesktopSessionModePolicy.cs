namespace FloatDesk.Core.Models;

public static class DesktopSessionModePolicy
{
    public static DesktopSessionSettings Normalize(DesktopSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var local = Resolve(settings, DesktopSessionBackendKind.LocalChild, settings.LocalChild);
        var vm = Resolve(settings, DesktopSessionBackendKind.HyperV, settings.HyperV);
        return settings with { LocalChild = local, HyperV = vm, Enabled = local.Enabled || vm.Enabled };
    }

    public static bool IsEnabled(DesktopSessionSettings settings, DesktopSessionBackendKind kind)
    {
        var normalized = Normalize(settings);
        return kind switch
        {
            DesktopSessionBackendKind.LocalChild => normalized.LocalChild!.Enabled,
            DesktopSessionBackendKind.HyperV => normalized.HyperV!.Enabled,
            _ => false
        };
    }

    private static BackendModeSettings Resolve(
        DesktopSessionSettings settings,
        DesktopSessionBackendKind kind,
        BackendModeSettings? mode)
    {
        var environments = settings.Environments.Where(environment => environment.Backend == kind).ToArray();
        if (mode is null)
            return new(settings.Enabled && environments.Length > 0, environments.FirstOrDefault()?.Id);
        return mode with
        {
            DefaultEnvironmentId = environments.Any(environment => environment.Id == mode.DefaultEnvironmentId)
                ? mode.DefaultEnvironmentId
                : null
        };
    }
}
