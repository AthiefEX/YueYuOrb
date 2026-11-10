using FloatDesk.Core.Models;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FloatDesk.Windows.DesktopSessions;

public interface IDesktopSessionControlHandler
{
    Task<DesktopSessionControlResponse> HandleAsync(
        DesktopSessionControlRequest request,
        CancellationToken cancellationToken);
}

public sealed class DesktopSessionControlServer :
    IAsyncDisposable
{
    private readonly IDesktopSessionControlHandler _handler;
    private readonly string? _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _acceptLoop;
    private bool _disposed;

    public DesktopSessionControlServer(
        IDesktopSessionControlHandler handler, string? pipeName = null)
    {
        _handler = handler ??
            throw new ArgumentNullException(nameof(handler));
        _pipeName = pipeName;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _acceptLoop ??= Task.Run(
            () => AcceptLoopAsync(_lifetime.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(
        CancellationToken cancellationToken)
    {
        var current = WindowsIdentity.GetCurrent();
        var user = current.User ??
            throw new InvalidOperationException(
                "The current Windows user is unavailable.");
        var sessionId = checked(
            (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = CreatePipe(
                _pipeName ?? DesktopSessionControlProtocol.GetPipeName(sessionId),
                user);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                var peer = NamedPipePeerIdentity.GetClientIdentity(pipe);
                if (peer.SessionId != sessionId ||
                    !string.Equals(
                        peer.UserSid,
                        user.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var response = await ProcessRequestAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);
                await SessionProtocolJson.WriteAsync(
                    pipe,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or
                InvalidOperationException or OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }
    }

    private async Task<DesktopSessionControlResponse> ProcessRequestAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        var request =
            await SessionProtocolJson.ReadAsync<
                DesktopSessionControlRequest>(
                pipe,
                cancellationToken).ConfigureAwait(false);
        if (request is null ||
            request.Version !=
                DesktopSessionControlProtocol.CurrentVersion)
        {
            return DesktopSessionControlResponse.Fail(
                "InvalidRequest",
                "The desktop session request is invalid.");
        }

        try
        {
            var caller=NamedPipePeerIdentity.GetClientIdentity(pipe);
            request=request with {VerifiedCaller=new(caller.ProcessId,caller.SessionId,caller.ProcessStartedAtUtc)};
            return await _handler.HandleAsync(
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return DesktopSessionControlResponse.Fail(
                "HandlerFailed",
                "The desktop session request could not be completed.");
        }
    }

    private static NamedPipeServerStream CreatePipe(
        string pipeName,
        SecurityIdentifier user)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                user,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(
                    WellKnownSidType.NetworkSid,
                    null),
                PipeAccessRights.FullControl,
                AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None);
    }
}
