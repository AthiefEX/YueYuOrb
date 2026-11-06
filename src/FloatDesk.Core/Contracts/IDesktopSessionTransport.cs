using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IDesktopSessionTransport
{
    Task<SessionTransportHandshake> ConnectAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task AcquireInputLeaseAsync(
        SessionHandle handle,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    Task RenewInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task ReleaseInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task CancelAsync(
        SessionHandle handle,
        Guid commandId,
        CancellationToken cancellationToken);

    Task<CommandResult> SendAsync(
        SessionHandle handle,
        SessionCommand command,
        CancellationToken cancellationToken);
}

public sealed record SessionTransportHandshake(
    bool Succeeded,
    Guid Generation,
    Guid LoginInstanceId,
    string? ErrorCode);
