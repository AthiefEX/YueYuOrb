using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IDesktopSessionBackend
{
    Task<SessionCapabilities> ProbeAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken);

    Task<PreparationResult> PrepareAsync(
        EnvironmentProfile profile,
        SessionStartupPolicy policy,
        CancellationToken cancellationToken);

    Task<SessionHandle> StartAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken);

    Task ShowAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task StopAsync(
        SessionHandle handle,
        SessionStopMode mode,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SessionBackendEvent> WatchAsync(
        Guid environmentId,
        CancellationToken cancellationToken);
}

public sealed record PreparationResult(
    bool Succeeded,
    bool RequiresNewLogin,
    SessionCapabilities Capabilities,
    string? ErrorCode);
