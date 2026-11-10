using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace FloatDesk.Windows.DesktopSessions;

public static class SessionBrokerProtocol
{
    public const int CurrentVersion = 1;
    public const string PipeName = "YueYuOrb.SessionBroker.v1";

    public static void ValidateRequest(SessionBrokerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != CurrentVersion ||
            request.EnvironmentId == Guid.Empty ||
            request.Generation == Guid.Empty ||
            request.WindowsSessionId == 0)
        {
            throw new InvalidOperationException("BrokerRequestInvalid");
        }
    }
}

public sealed record SessionBrokerRequest(
    int Version,
    Guid EnvironmentId,
    Guid Generation,
    uint WindowsSessionId);

public sealed record SessionBrokerResponse(
    int Version,
    bool Accepted,
    Guid LoginInstanceId,
    string? UserSid,
    uint ProcessId,
    DateTimeOffset ProcessStartedAtUtc,
    byte[]? BootstrapSecret,
    string? ErrorCode);

public interface ISessionBrokerAgentLauncher
{
    Task<SessionBrokerResponse> LaunchAsync(
        SessionBrokerRequest request,
        SessionPeerIdentity caller,
        CancellationToken cancellationToken);
}

public sealed class SessionBrokerClient : IChildSessionAgentBootstrapper
{
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    public SessionBrokerClient(
        string pipeName = SessionBrokerProtocol.PipeName,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<ChildSessionAgentBootstrap> LaunchAsync(
        Guid environmentId,
        Guid generation,
        uint windowsSessionId,
        CancellationToken cancellationToken)
    {
        var request = new SessionBrokerRequest(
            SessionBrokerProtocol.CurrentVersion,
            environmentId,
            generation,
            windowsSessionId);
        SessionBrokerProtocol.ValidateRequest(request);
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        await SessionProtocolJson.WriteAsync(
            pipe,
            request,
            timeout.Token).ConfigureAwait(false);
        var response =
            await SessionProtocolJson.ReadAsync<SessionBrokerResponse>(
                pipe,
                timeout.Token).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "The session broker returned no response.");
        if (!response.Accepted)
        {
            throw new InvalidOperationException(
                response.ErrorCode ?? "SessionBrokerRejected");
        }

        if (response.Version != SessionBrokerProtocol.CurrentVersion ||
            response.LoginInstanceId == Guid.Empty ||
            response.ProcessId == 0 ||
            response.ProcessStartedAtUtc == default ||
            string.IsNullOrWhiteSpace(response.UserSid) ||
            response.BootstrapSecret is not
            {
                Length: SessionProtocol.BootstrapSecretSize
            })
        {
            if (response.BootstrapSecret is not null)
                CryptographicOperations.ZeroMemory(response.BootstrapSecret);
            throw new InvalidOperationException(
                "The session broker response is invalid.");
        }

        return new ChildSessionAgentBootstrap(
            response.LoginInstanceId,
            response.UserSid,
            response.ProcessId,
            response.ProcessStartedAtUtc,
            response.BootstrapSecret);
    }
}

public sealed class SessionBrokerServer
{
    private readonly ISessionBrokerAgentLauncher _launcher;
    private readonly string _pipeName;

    public SessionBrokerServer(
        ISessionBrokerAgentLauncher launcher,
        string pipeName = SessionBrokerProtocol.PipeName)
    {
        _launcher = launcher ??
            throw new ArgumentNullException(nameof(launcher));
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipeServer(_pipeName);
            await pipe.WaitForConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await ServeAsync(pipe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or
                InvalidOperationException or
                UnauthorizedAccessException or
                OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }
        }
    }

    private async Task ServeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var handshakeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var caller = NamedPipePeerIdentity.GetClientIdentity(pipe);
        SessionBrokerRequest? request;
        try
        {
            request = await SessionProtocolJson.ReadAsync<SessionBrokerRequest>(
                pipe,
                handshakeTimeout.Token).ConfigureAwait(false);
            if (request is null)
                throw new InvalidOperationException("BrokerRequestMissing");
            SessionBrokerProtocol.ValidateRequest(request);
            if (caller.SessionId == request.WindowsSessionId)
            {
                throw new InvalidOperationException(
                    "The caller cannot bootstrap an agent into its own session.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.Text.Json.JsonException or
            IOException or
            OperationCanceledException)
        {
            await SessionProtocolJson.WriteAsync(
                pipe,
                Rejected("BrokerRequestInvalid"),
                handshakeTimeout.Token).ConfigureAwait(false);
            return;
        }

        SessionBrokerResponse response;
        try
        {
            response = await _launcher.LaunchAsync(
                request,
                caller,
                handshakeTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            UnauthorizedAccessException or
            IOException or
            OperationCanceledException)
        {
            response = Rejected(
                exception is OperationCanceledException
                    ? "BrokerLaunchTimedOut"
                    : exception.Message);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            response = Rejected($"BrokerWin32Error:{exception.NativeErrorCode}");
        }

        try
        {
            await SessionProtocolJson.WriteAsync(
                pipe,
                response,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (response.BootstrapSecret is not null)
                CryptographicOperations.ZeroMemory(response.BootstrapSecret);
        }
    }

    private static SessionBrokerResponse Rejected(string errorCode) =>
        new(
            SessionBrokerProtocol.CurrentVersion,
            false,
            Guid.Empty,
            null,
            0,
            default,
            null,
            errorCode);

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(
                WellKnownSidType.AuthenticatedUserSid,
                null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
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
