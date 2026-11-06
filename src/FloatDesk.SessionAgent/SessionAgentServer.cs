using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using Microsoft.Win32.SafeHandles;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FloatDesk.SessionAgent;

internal static class SessionAgentServer
{
    public static async Task RunAsync(
        SessionAgentOptions options,
        ReadOnlyMemory<byte> bootstrapSecret)
    {
        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User?.Value != options.Binding.UserSid)
        {
            throw new InvalidOperationException(
                "The session agent is not running as the configured user.");
        }

        var currentSession =
            (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        if (currentSession != options.Binding.WindowsSessionId)
        {
            throw new InvalidOperationException(
                "The session agent is not running in the configured session.");
        }

        while (true)
        {
            using var server = CreatePipeServer(options.PipeName);
            await server.WaitForConnectionAsync().ConfigureAwait(false);
            try
            {
                await ServeAsync(
                    server,
                    options,
                    bootstrapSecret).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or
                OperationCanceledException or
                InvalidOperationException or
                UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task ServeAsync(
        NamedPipeServerStream pipe,
        SessionAgentOptions options,
        ReadOnlyMemory<byte> bootstrapSecret)
    {
        using var timeout =
            new CancellationTokenSource(SessionProtocol.HandshakeTimeout);
        var peer = NamedPipePeerIdentity.GetClientIdentity(pipe);
        SessionClientHello? hello;
        try
        {
            hello = await SessionProtocolJson.ReadAsync<SessionClientHello>(
                pipe,
                timeout.Token).ConfigureAwait(false);
            if (hello is null)
                throw new InvalidOperationException("Handshake hello missing.");
            SessionConnectionAuthorization.Validate(
                options.Binding,
                peer,
                hello);
        }
        catch (Exception exception) when (
            exception is IOException or
            OperationCanceledException or
            InvalidOperationException)
        {
            await SessionProtocolJson.WriteAsync(
                pipe,
                new SessionServerHello(
                    SessionProtocol.CurrentVersion,
                    false,
                    null,
                    [],
                    [],
                    "IdentityRejected"),
                timeout.Token).ConfigureAwait(false);
            return;
        }

        var serverNonce = SessionHandshake.CreateNonce();
        var serverProof = SessionHandshake.CreateProof(
            bootstrapSecret.Span,
            hello.ClientNonce,
            serverNonce,
            options.Binding,
            serverToClient: true);
        await SessionProtocolJson.WriteAsync(
            pipe,
            new SessionServerHello(
                SessionProtocol.CurrentVersion,
                true,
                options.Binding,
                serverNonce,
                serverProof,
                null),
            timeout.Token).ConfigureAwait(false);
        var proof = await SessionProtocolJson.ReadAsync<SessionClientProof>(
            pipe,
            timeout.Token).ConfigureAwait(false) ??
            throw new InvalidOperationException("Client proof missing.");
        var expectedProof = SessionHandshake.CreateProof(
            bootstrapSecret.Span,
            hello.ClientNonce,
            serverNonce,
            options.Binding,
            serverToClient: false);
        var accepted = proof is not null &&
            SessionHandshake.VerifyProof(
                expectedProof,
                proof.Proof);
        await SessionProtocolJson.WriteAsync(
            pipe,
            new SessionHandshakeAccepted(
                accepted,
                accepted ? null : "AuthenticationRejected"),
            timeout.Token).ConfigureAwait(false);
        if (!accepted)
            return;

        await RunProtocolLoopAsync(
            pipe,
            options.Binding).ConfigureAwait(false);
    }

    internal static async Task RunProtocolLoopAsync(
        Stream pipe,
        SessionAgentBinding binding)
    {
        var writeGate = new SemaphoreSlim(1, 1);
        var lease = new SessionInputLease();
        var executor = new ProtocolInputExecutor();
        var dispatcher = new SessionCommandDispatcher(binding, executor);
        var activeCommands = new ConcurrentDictionary<Guid, Task>();
        using var lifetime = new CancellationTokenSource();
        var leaseMonitor = MonitorLeaseAsync(
            lease,
            dispatcher,
            lifetime.Token);
        try
        {
            while (true)
            {
                var request = await SessionProtocolJson.ReadAsync<SessionWireEnvelope>(
                    pipe,
                    lifetime.Token).ConfigureAwait(false);
                if (request is null)
                    break;
                switch (request.Kind)
                {
                    case "acquire-lease":
                        await WriteResponseAsync(
                            pipe,
                            writeGate,
                            LeaseResponse(request, lease.Acquire(request.LeaseSeconds)),
                            lifetime.Token).ConfigureAwait(false);
                        break;
                    case "renew-lease":
                        await WriteResponseAsync(
                            pipe,
                            writeGate,
                            LeaseResponse(request, lease.Renew()),
                            lifetime.Token).ConfigureAwait(false);
                        break;
                    case "release-lease":
                        lease.Release();
                        await WriteResponseAsync(
                            pipe,
                            writeGate,
                            Response(request, true, null),
                            lifetime.Token).ConfigureAwait(false);
                        break;
                    case "cancel":
                        await dispatcher.CancelAsync(
                            request.CommandId,
                            lifetime.Token).ConfigureAwait(false);
                        lease.Release();
                        await WriteResponseAsync(
                            pipe,
                            writeGate,
                            Response(request, true, null),
                            lifetime.Token).ConfigureAwait(false);
                        break;
                    case "command":
                        if (request.Command is null)
                        {
                            await WriteResponseAsync(
                                pipe,
                                writeGate,
                                Response(
                                    request,
                                    false,
                                    "InvalidCommand"),
                                lifetime.Token).ConfigureAwait(false);
                            break;
                        }

                        var command = request.Command;
                        if (!lease.IsExpired)
                        {
                            activeCommands[command.CommandId] =
                                ProcessCommandAsync(
                                    dispatcher,
                                    command,
                                    pipe,
                                    writeGate,
                                    request.RequestId,
                                    lifetime.Token);
                        }
                        else
                        {
                            await WriteResponseAsync(
                                pipe,
                                writeGate,
                                new SessionWireEnvelope(
                                    "command-result",
                                    request.RequestId,
                                    command.CommandId,
                                    null,
                                    0,
                                    true,
                                    false,
                                    false,
                                    "InputLeaseExpired"),
                                lifetime.Token).ConfigureAwait(false);
                        }

                        break;
                    default:
                        await WriteResponseAsync(
                            pipe,
                            writeGate,
                            Response(
                                request,
                                false,
                                "UnsupportedRequest"),
                            lifetime.Token).ConfigureAwait(false);
                        break;
                }
            }

            await Task.WhenAll(activeCommands.Values).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            lease.Release();
            await dispatcher.CancelAllAsync(CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                await leaseMonitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            writeGate.Dispose();
        }
    }

    private static async Task ProcessCommandAsync(
        SessionCommandDispatcher dispatcher,
        SessionCommand command,
        Stream pipe,
        SemaphoreSlim writeGate,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync(
            command,
            cancellationToken).ConfigureAwait(false);
        await WriteResponseAsync(
            pipe,
            writeGate,
            new SessionWireEnvelope(
                "command-result",
                requestId,
                command.CommandId,
                null,
                0,
                true,
                result.Succeeded,
                result.OutcomeUnknown,
                result.ErrorCode),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task MonitorLeaseAsync(
        SessionInputLease lease,
        SessionCommandDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    cancellationToken).ConfigureAwait(false);
                if (lease.Expire())
                {
                    await dispatcher.CancelAllAsync(
                        CancellationToken.None).ConfigureAwait(false);
                    lease.Release();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static SessionWireEnvelope LeaseResponse(
        SessionWireEnvelope request,
        bool accepted) =>
        Response(
            request,
            accepted,
            accepted ? null : "InputLeaseRejected");

    private static SessionWireEnvelope Response(
        SessionWireEnvelope request,
        bool accepted,
        string? errorCode) =>
        new(
            "control-result",
            request.RequestId,
            request.CommandId,
            null,
            0,
            accepted,
            accepted,
            false,
            errorCode);

    private static async Task WriteResponseAsync(
        Stream pipe,
        SemaphoreSlim writeGate,
        SessionWireEnvelope response,
        CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SessionProtocolJson.WriteAsync(
                pipe,
                response,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        var currentIdentity = WindowsIdentity.GetCurrent();
        if (currentIdentity.User is null)
            throw new InvalidOperationException("Current user SID unavailable.");
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                currentIdentity.User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
        security.AddAccessRule(
            new PipeAccessRule(
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

    private sealed class ProtocolInputExecutor : ISessionInputExecutor
    {
        public Task<CommandResult> ExecuteAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(
                command.CommandId,
                false,
                false,
                "AutomationExecutorUnavailable"));

        public Task ReleaseHeldInputAsync(
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
