using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public static class DesktopSessionStartCoordinator
{
    public static Task<SessionHandle> StartAsync(
        IDesktopSessionBackend backend,
        EnvironmentProfile profile,
        PreparationResult preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(profile);
        DesktopSessionPreparationGuard.RequireSuccessful(preparation);
        return backend.StartAsync(profile, preparation, cancellationToken);
    }
}
