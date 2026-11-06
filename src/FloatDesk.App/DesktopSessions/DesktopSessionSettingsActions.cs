namespace FloatDesk.App.DesktopSessions;

public sealed record DesktopSessionActionStatus(
    bool Success,
    string Message);

public interface IDesktopSessionSettingsActions
{
    bool IsRuntimeAvailable { get; }

    Task<DesktopSessionActionStatus> StartEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken);

    Task<DesktopSessionActionStatus> StopEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken);

    Task<DesktopSessionActionStatus> RunTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken);

    Task<DesktopSessionActionStatus> CancelLatestRunAsync(
        CancellationToken cancellationToken);

    IReadOnlyList<string> GetRecentActivity();
}
