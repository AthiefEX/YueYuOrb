using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed class TransportStartupApplicationRunner :
    IStartupApplicationRunner
{
    private readonly IDesktopSessionTransport _transport;
    private readonly TimeProvider _timeProvider;

    public TransportStartupApplicationRunner(
        IDesktopSessionTransport transport,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<StartupRunResult> RunAsync(
        SessionHandle handle,
        SessionStartupEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(entry);
        var command = new SessionCommand(
            Guid.NewGuid(),
            handle,
            SessionCommandKind.LaunchApplication,
            SessionStartupCommandCodec.ToParameters(entry),
            _timeProvider.GetUtcNow().AddSeconds(60));
        var result = await _transport.SendAsync(
            handle,
            command,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? StartupRunResult.Success
            : StartupRunResult.Failed(
                result.ErrorCode ?? "StartupApplicationFailed",
                result.OutcomeUnknown
                    ? "The guest did not acknowledge the launch."
                    : null);
    }
}
