using FloatDesk.Core.Models;
using System.IO.Pipes;
using System.Security.Principal;

namespace FloatDesk.Windows.DesktopSessions;

public static class DesktopSessionControlClient
{
    public static async Task<DesktopSessionControlResponse> SendAsync(
        DesktopSessionControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = WindowsIdentity.GetCurrent();
        var userSid = current.User?.Value ??
            throw new InvalidOperationException(
                "The current Windows user is unavailable.");
        var sessionId = checked(
            (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId);
        await using var pipe = new NamedPipeClientStream(
            ".",
            DesktopSessionControlProtocol.GetPipeName(sessionId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var server = NamedPipePeerIdentity.GetServerIdentity(pipe);
        if (server.SessionId != sessionId ||
            !string.Equals(
                server.UserSid,
                userSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The desktop-session control pipe is not owned by the current user and session.");
        }

        await SessionProtocolJson.WriteAsync(
            pipe,
            request,
            cancellationToken).ConfigureAwait(false);
        return await SessionProtocolJson.ReadAsync<
                DesktopSessionControlResponse>(
                pipe,
                cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "The desktop-session control response is missing.");
    }
}
