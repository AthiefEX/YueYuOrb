using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace FloatDesk.Windows.DesktopSessions;

public interface IHyperVSocketConnector
{
    Task<Stream> ConnectAsync(
        Guid vmId,
        Guid serviceId,
        CancellationToken cancellationToken);
}

public sealed class WindowsHyperVSocketConnector :
    IHyperVSocketConnector
{
    private const int AddressFamilyHyperV = 34;
    private const int ProtocolRaw = 1;
    private const int HyperVAddressSize = 36;

    public async Task<Stream> ConnectAsync(
        Guid vmId,
        Guid serviceId,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Hyper-V sockets are only available on Windows.");
        }

        if (vmId == Guid.Empty || serviceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Hyper-V socket endpoints require VM and service IDs.");
        }

        var socket = new Socket(
            (AddressFamily)AddressFamilyHyperV,
            SocketType.Stream,
            (ProtocolType)ProtocolRaw);
        try
        {
            using var registration = cancellationToken.Register(socket.Dispose);
            await Task.Run(() => socket.Connect(new HyperVSocketEndPoint(vmId, serviceId)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested &&
            exception is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            throw new OperationCanceledException("Hyper-V guest connection timed out or was cancelled.", exception, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class HyperVSocketEndPoint(
        Guid vmId,
        Guid serviceId) : EndPoint
    {
        public override AddressFamily AddressFamily =>
            (AddressFamily)AddressFamilyHyperV;

        public override SocketAddress Serialize()
        {
            var address = new SocketAddress(
                AddressFamily,
                HyperVAddressSize);
            var buffer = address.Buffer.Span;
            buffer.Clear();
            buffer[0] = AddressFamilyHyperV;
            vmId.TryWriteBytes(buffer.Slice(4, 16));
            serviceId.TryWriteBytes(buffer.Slice(20, 16));
            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress)
        {
            if (socketAddress.Family != AddressFamily || socketAddress.Size < HyperVAddressSize)
                throw new ArgumentException("Invalid Hyper-V socket address.", nameof(socketAddress));
            return new HyperVSocketEndPoint(new Guid(socketAddress.Buffer.Span.Slice(4, 16)), new Guid(socketAddress.Buffer.Span.Slice(20, 16)));
        }
    }
}

public sealed class HyperVSocketTransport :
    IDesktopSessionTransport,
    IAsyncDisposable
{
    private readonly IHyperVSocketConnector _connector;
    private readonly HyperVAgentEndpointRegistry _endpoints;
    private readonly IHyperVPairingSecretStore _secretStore;
    private readonly TimeSpan _handshakeTimeout;
    private readonly HyperVPairingHandshake _pairingHandshake = new();
    private readonly ConcurrentDictionary<Guid, SessionStreamConnection>
        _connections = new();

    public HyperVSocketTransport(
        IHyperVSocketConnector connector,
        HyperVAgentEndpointRegistry endpoints,
        IHyperVPairingSecretStore secretStore,
        TimeSpan? handshakeTimeout = null)
    {
        _connector = connector ??
            throw new ArgumentNullException(nameof(connector));
        _endpoints = endpoints ??
            throw new ArgumentNullException(nameof(endpoints));
        _secretStore = secretStore ??
            throw new ArgumentNullException(nameof(secretStore));
        _handshakeTimeout =
            handshakeTimeout ?? SessionProtocol.HandshakeTimeout;
        if (_handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handshakeTimeout));
        }
    }

    public async Task<SessionTransportHandshake> ConnectAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (_connections.TryGetValue(
                handle.EnvironmentId,
                out var existing))
        {
            return existing.Handshake;
        }

        if (!_endpoints.TryGet(
                handle.EnvironmentId,
                out var endpoint) ||
            endpoint is null)
        {
            throw new InvalidOperationException(
                "The Hyper-V guest endpoint is not prepared.");
        }

        var secret = await _secretStore.LoadAsync(
            endpoint.VmId,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "The Hyper-V guest pairing secret is missing.");
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);
        Stream? stream = null;
        try
        {
            stream = await _connector.ConnectAsync(
                endpoint.VmId,
                endpoint.ServiceId,
                timeout.Token).ConfigureAwait(false);
            var hello = HyperVPairingProtocol.CreateClientHello(
                endpoint.VmId,
                endpoint.ServiceId);
            await SessionProtocolJson.WriteAsync(
                stream,
                hello,
                timeout.Token).ConfigureAwait(false);
            var serverHello =
                await SessionProtocolJson.ReadAsync<
                    HyperVPairingServerHello>(
                    stream,
                    timeout.Token).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "The Hyper-V guest did not return a handshake.");
            if (!_pairingHandshake.VerifyServerHello(
                    secret,
                    hello,
                    serverHello))
            {
                throw new InvalidOperationException(
                    serverHello.ErrorCode ??
                    "The Hyper-V guest pairing proof is invalid.");
            }

            var binding = new HyperVPairingBinding(
                handle.EnvironmentId,
                handle.Generation,
                handle.LoginInstanceId,
                handle.WindowsSessionId,
                endpoint.UserSid,
                endpoint.BootId,
                endpoint.LoginTimeUtc);
            var proof = HyperVPairingProtocol.CreateClientProof(
                secret,
                hello,
                serverHello,
                binding);
            await SessionProtocolJson.WriteAsync(
                stream,
                new HyperVPairingClientProof(proof, binding),
                timeout.Token).ConfigureAwait(false);
            var accepted =
                await SessionProtocolJson.ReadAsync<
                    HyperVPairingAccepted>(
                    stream,
                    timeout.Token).ConfigureAwait(false);
            if (accepted is null || !accepted.Accepted)
            {
                throw new InvalidOperationException(
                    accepted?.ErrorCode ??
                    "The Hyper-V guest authentication failed.");
            }

            var connection = new SessionStreamConnection(stream, handle);
            connection.StartReading();
            if (!_connections.TryAdd(
                    handle.EnvironmentId,
                    connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return _connections[handle.EnvironmentId].Handshake;
            }

            return connection.Handshake;
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public Task AcquireInputLeaseAsync(
        SessionHandle handle,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        SendControlAsync(
            handle,
            "acquire-lease",
            Guid.Empty,
            checked((int)Math.Ceiling(leaseDuration.TotalSeconds)),
            cancellationToken);

    public Task RenewInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken) =>
        SendControlAsync(
            handle,
            "renew-lease",
            Guid.Empty,
            0,
            cancellationToken);

    public Task ReleaseInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken) =>
        SendControlAsync(
            handle,
            "release-lease",
            Guid.Empty,
            0,
            cancellationToken);

    public Task CancelAsync(
        SessionHandle handle,
        Guid commandId,
        CancellationToken cancellationToken) =>
        SendControlAsync(
            handle,
            "cancel",
            commandId,
            0,
            cancellationToken);

    public async Task<CommandResult> SendAsync(
        SessionHandle handle,
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var response = await GetConnection(handle).RequestAsync(
            new SessionWireEnvelope(
                "command",
                command.CommandId,
                command.CommandId,
                command,
                0,
                false,
                false,
                false,
                null),
            command.CommandId,
            cancellationToken).ConfigureAwait(false);
        return new CommandResult(
            response.CommandId == Guid.Empty
                ? command.CommandId
                : response.CommandId,
            response.Succeeded,
            response.OutcomeUnknown,
            response.ErrorCode) { DataJson = response.DataJson };
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
            await connection.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();
    }

    private async Task SendControlAsync(
        SessionHandle handle,
        string kind,
        Guid commandId,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var response = await GetConnection(handle).RequestAsync(
            new SessionWireEnvelope(
                kind,
                requestId,
                commandId,
                null,
                leaseSeconds,
                false,
                false,
                false,
                null),
            requestId,
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted)
        {
            throw new InvalidOperationException(
                response.ErrorCode ??
                $"Session control '{kind}' failed.");
        }
    }

    private SessionStreamConnection GetConnection(SessionHandle handle) =>
        _connections.TryGetValue(
            handle.EnvironmentId,
            out var connection)
            ? connection
            : throw new InvalidOperationException(
                "The Hyper-V session transport is not connected.");

    private sealed class SessionStreamConnection : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentDictionary<
            Guid,
            TaskCompletionSource<SessionWireEnvelope>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _readLoop;

        public SessionStreamConnection(
            Stream stream,
            SessionHandle handle)
        {
            _stream = stream;
            Handshake = new SessionTransportHandshake(
                true,
                handle.Generation,
                handle.LoginInstanceId,
                null);
        }

        public SessionTransportHandshake Handshake { get; }

        public void StartReading() =>
            _readLoop = ReadLoopAsync();

        public async Task<SessionWireEnvelope> RequestAsync(
            SessionWireEnvelope request,
            Guid responseId,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<SessionWireEnvelope>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(responseId, completion))
            {
                throw new InvalidOperationException(
                    "A session request with the same identity is already active.");
            }

            try
            {
                await _writeGate.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await SessionProtocolJson.WriteAsync(
                        _stream,
                        request,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }

                return await completion.Task
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(responseId, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(
                    new IOException(
                        "The Hyper-V session connection closed."));
            }

            await _stream.DisposeAsync().ConfigureAwait(false);
            if (_readLoop is not null)
            {
                try
                {
                    await _readLoop.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _lifetime.Dispose();
            _writeGate.Dispose();
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var response =
                        await SessionProtocolJson.ReadAsync<
                            SessionWireEnvelope>(
                            _stream,
                            _lifetime.Token).ConfigureAwait(false);
                    if (response is null)
                        break;
                    if (_pending.TryGetValue(
                            response.RequestId,
                            out var completion))
                    {
                        completion.TrySetResult(response);
                    }
                }
            }
            catch (OperationCanceledException)
                when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                foreach (var pending in _pending.Values)
                    pending.TrySetException(exception);
            }
            finally
            {
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(
                        new IOException(
                            "The Hyper-V session connection closed."));
                }
            }
        }
    }
}
