namespace FloatDesk.Core.Contracts;

public sealed record ChildSessionAgentBootstrap(
    Guid LoginInstanceId,
    string UserSid,
    uint ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    byte[] BootstrapSecret);

public interface IChildSessionAgentBootstrapper
{
    Task<ChildSessionAgentBootstrap> LaunchAsync(
        Guid environmentId,
        Guid generation,
        uint windowsSessionId,
        CancellationToken cancellationToken);
}
