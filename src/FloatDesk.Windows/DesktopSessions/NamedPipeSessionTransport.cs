using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public static class SessionProtocol
{
    public const int CurrentVersion = 1;
    public const int MaxFrameBytes = 1024 * 1024;
    public const int NonceSize = 16;
    public const int BootstrapSecretSize = 32;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    public static string GetPipeName(Guid environmentId) =>
        $"YueYuOrb.SessionAgent.{environmentId:N}";
}

public sealed record SessionAgentBinding(
    Guid EnvironmentId,
    Guid Generation,
    Guid LoginInstanceId,
    uint WindowsSessionId,
    string UserSid);

public sealed record SessionPeerIdentity(
    uint ProcessId,
    uint SessionId,
    string UserSid,
    DateTimeOffset ProcessStartedAtUtc = default);

public sealed record SessionClientHello(
    int Version,
    Guid EnvironmentId,
    Guid Generation,
    Guid LoginInstanceId,
    uint WindowsSessionId,
    byte[] ClientNonce);

public sealed record SessionServerHello(
    int Version,
    bool Accepted,
    SessionAgentBinding? Binding,
    byte[] ServerNonce,
    byte[] ServerProof,
    string? ErrorCode);

public sealed record SessionClientProof(byte[] Proof);

public sealed record SessionHandshakeAccepted(
    bool Accepted,
    string? ErrorCode);

public sealed record SessionWireEnvelope(
    string Kind,
    Guid RequestId,
    Guid CommandId,
    SessionCommand? Command,
    int LeaseSeconds,
    bool Accepted,
    bool Succeeded,
    bool OutcomeUnknown,
    string? ErrorCode)
{
    public string? DataJson { get; init; }
}

public static class SessionConnectionAuthorization
{
    public static void ValidateAgentServerPeer(
        SessionAgentBinding binding,
        SessionPeerIdentity peer,
        SessionClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(hello);
        if (binding.WindowsSessionId == 0 ||
            peer.SessionId != binding.WindowsSessionId)
        {
            throw new InvalidOperationException(
                "The pipe peer is not in the configured session.");
        }

        ValidateBindingUserAndClientIdentity(binding, peer, hello);
    }

    public static void ValidateHostClientPeer(
        SessionAgentBinding binding,
        uint hostWindowsSessionId,
        SessionPeerIdentity peer,
        SessionClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(hello);
        if (hostWindowsSessionId == 0 ||
            hostWindowsSessionId == binding.WindowsSessionId ||
            peer.SessionId != hostWindowsSessionId)
        {
            throw new InvalidOperationException(
                "The pipe peer is not the configured host session.");
        }

        ValidateBindingUserAndClientIdentity(binding, peer, hello);
    }

    private static void ValidateBindingUserAndClientIdentity(
        SessionAgentBinding binding,
        SessionPeerIdentity peer,
        SessionClientHello hello)
    {
        if (!string.Equals(
                peer.UserSid,
                binding.UserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The pipe peer is not the configured user.");
        }

        if (hello.Version != SessionProtocol.CurrentVersion ||
            hello.EnvironmentId != binding.EnvironmentId ||
            hello.Generation != binding.Generation ||
            hello.LoginInstanceId != binding.LoginInstanceId ||
            hello.WindowsSessionId != binding.WindowsSessionId ||
            hello.ClientNonce.Length != SessionProtocol.NonceSize)
        {
            throw new InvalidOperationException(
                "The session client identity is invalid.");
        }
    }
}

public static class SessionHandshake
{
    public static byte[] CreateNonce() =>
        RandomNumberGenerator.GetBytes(SessionProtocol.NonceSize);

    public static byte[] CreateProof(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> clientNonce,
        ReadOnlySpan<byte> serverNonce,
        SessionAgentBinding binding,
        bool serverToClient)
    {
        if (secret.Length != SessionProtocol.BootstrapSecretSize)
        {
            throw new ArgumentException(
                "The bootstrap secret must be 32 bytes.",
                nameof(secret));
        }

        if (clientNonce.Length != SessionProtocol.NonceSize ||
            serverNonce.Length != SessionProtocol.NonceSize)
        {
            throw new ArgumentException(
                "Handshake nonces must be 16 bytes.");
        }

        ArgumentNullException.ThrowIfNull(binding);
        var prefix = System.Text.Encoding.ASCII.GetBytes(
            serverToClient ? "server\0" : "client\0");
        var userSid = System.Text.Encoding.UTF8.GetBytes(binding.UserSid);
        var input = new byte[
            prefix.Length +
            SessionProtocol.NonceSize * 2 +
            16 * 3 +
            4 +
            userSid.Length];
        var offset = 0;
        prefix.CopyTo(input, offset);
        offset += prefix.Length;
        clientNonce.CopyTo(input.AsSpan(offset));
        offset += SessionProtocol.NonceSize;
        serverNonce.CopyTo(input.AsSpan(offset));
        offset += SessionProtocol.NonceSize;
        binding.EnvironmentId.TryWriteBytes(input.AsSpan(offset));
        offset += 16;
        binding.Generation.TryWriteBytes(input.AsSpan(offset));
        offset += 16;
        binding.LoginInstanceId.TryWriteBytes(input.AsSpan(offset));
        offset += 16;
        BinaryPrimitives.WriteUInt32LittleEndian(
            input.AsSpan(offset),
            binding.WindowsSessionId);
        offset += 4;
        userSid.CopyTo(input, offset);

        using var hmac = new HMACSHA256(secret.ToArray());
        return hmac.ComputeHash(input);
    }

    public static bool VerifyProof(
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> actual) =>
        expected.Length == actual.Length &&
        CryptographicOperations.FixedTimeEquals(expected, actual);
}

public static class SessionFrameCodec
{
    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (payload.IsEmpty ||
            payload.Length > SessionProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException(
                "Session protocol frames must be between 1 byte and 1 MiB.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var first = await stream.ReadAsync(
            header.AsMemory(0, 1),
            cancellationToken).ConfigureAwait(false);
        if (first == 0)
            return null;
        await stream.ReadExactlyAsync(
            header.AsMemory(1),
            cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > SessionProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException(
                "Session protocol frame length is invalid.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(
            payload,
            cancellationToken).ConfigureAwait(false);
        return payload;
    }
}

public static class SessionProtocolJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        await SessionFrameCodec.WriteAsync(
            stream,
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await SessionFrameCodec.ReadAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        return payload is null
            ? default
            : JsonSerializer.Deserialize<T>(payload, Options);
    }
}

public static class NamedPipePeerIdentity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static SessionPeerIdentity GetClientIdentity(
        NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out var processId))
        {
            throw new InvalidOperationException(
                "The pipe client process could not be identified.");
        }

        return GetIdentity(processId);
    }

    public static SessionPeerIdentity GetServerIdentity(
        NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(
                pipe.SafePipeHandle,
                out var processId))
        {
            throw new InvalidOperationException(
                "The pipe server process could not be identified.");
        }

        return GetIdentity(processId);
    }

    private static SessionPeerIdentity GetIdentity(uint processId)
    {
        using var process = Process.GetProcessById(checked((int)processId));
        var sessionId = checked((uint)process.SessionId);
        var processHandle = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == IntPtr.Zero)
            throw new InvalidOperationException("OpenProcess failed.");
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
                throw new InvalidOperationException("OpenProcessToken failed.");
            using var token = new SafeAccessTokenHandle(tokenHandle);
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            var sid = identity.User?.Value ??
                throw new InvalidOperationException(
                    "The pipe peer user SID is unavailable.");
            var startedAt = process.StartTime.ToUniversalTime();
            return new SessionPeerIdentity(
                processId,
                sessionId,
                sid,
                new DateTimeOffset(startedAt, TimeSpan.Zero));
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public sealed class NamedPipeSessionTransport :
    IDesktopSessionTransport,
    IAsyncDisposable
{
    private readonly byte[]? _bootstrapSecret;
    private readonly Func<SessionHandle, ChildSessionAgentBootstrap>?
        _bootstrapProvider;
    private readonly TimeSpan _handshakeTimeout;
    private readonly ConcurrentDictionary<Guid, SessionConnection> _connections =
        new();

    public NamedPipeSessionTransport(
        ReadOnlySpan<byte> bootstrapSecret,
        TimeSpan? handshakeTimeout = null)
    {
        if (bootstrapSecret.Length != SessionProtocol.BootstrapSecretSize)
        {
            throw new ArgumentException(
                "The bootstrap secret must be 32 bytes.",
                nameof(bootstrapSecret));
        }

        _bootstrapSecret = bootstrapSecret.ToArray();
        _handshakeTimeout =
            handshakeTimeout ?? SessionProtocol.HandshakeTimeout;
        if (_handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(handshakeTimeout));
        }
    }

    public NamedPipeSessionTransport(
        Func<SessionHandle, ChildSessionAgentBootstrap> bootstrapProvider,
        TimeSpan? handshakeTimeout = null)
    {
        _bootstrapProvider = bootstrapProvider ??
            throw new ArgumentNullException(nameof(bootstrapProvider));
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

        var bootstrap = _bootstrapProvider?.Invoke(handle);
        var secret = bootstrap?.BootstrapSecret.ToArray() ??
            _bootstrapSecret?.ToArray() ??
            throw new InvalidOperationException(
                "The session agent bootstrap secret is unavailable.");
        if (secret.Length != SessionProtocol.BootstrapSecretSize)
        {
            CryptographicOperations.ZeroMemory(secret);
            if (bootstrap is not null)
                CryptographicOperations.ZeroMemory(
                    bootstrap.BootstrapSecret);
            throw new InvalidOperationException(
                "The session agent bootstrap secret has an invalid length.");
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_handshakeTimeout);
        var stream = new NamedPipeClientStream(
            ".",
            SessionProtocol.GetPipeName(handle.EnvironmentId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var peer = NamedPipePeerIdentity.GetServerIdentity(stream);
            if (peer.SessionId != handle.WindowsSessionId)
            {
                throw new InvalidOperationException(
                    "The session agent is running in the wrong Windows session.");
            }
            if (bootstrap is not null &&
                (peer.ProcessId != bootstrap.ProcessId ||
                 peer.ProcessStartedAtUtc == default ||
                 Math.Abs(
                     (peer.ProcessStartedAtUtc -
                      bootstrap.ProcessStartedAtUtc).TotalSeconds) > 2 ||
                 !string.Equals(
                     peer.UserSid,
                     bootstrap.UserSid,
                     StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "The session agent process identity does not match the broker launch.");
            }

            var clientNonce = SessionHandshake.CreateNonce();
            var hello = new SessionClientHello(
                SessionProtocol.CurrentVersion,
                handle.EnvironmentId,
                handle.Generation,
                handle.LoginInstanceId,
                handle.WindowsSessionId,
                clientNonce);
            await SessionProtocolJson.WriteAsync(
                stream,
                hello,
                timeout.Token).ConfigureAwait(false);
            var serverHello = await SessionProtocolJson.ReadAsync<SessionServerHello>(
                stream,
                timeout.Token).ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    "The session agent did not return a handshake.");
            if (!serverHello.Accepted ||
                serverHello.Binding is null ||
                serverHello.Binding.EnvironmentId != handle.EnvironmentId ||
                serverHello.Binding.Generation != handle.Generation ||
                serverHello.Binding.LoginInstanceId != handle.LoginInstanceId ||
                serverHello.Binding.WindowsSessionId != handle.WindowsSessionId)
            {
                throw new InvalidOperationException(
                    serverHello.ErrorCode ?? "Session handshake rejected.");
            }

            SessionConnectionAuthorization.ValidateAgentServerPeer(
                serverHello.Binding,
                peer,
                hello);
            var expectedServerProof = SessionHandshake.CreateProof(
                secret,
                clientNonce,
                serverHello.ServerNonce,
                serverHello.Binding,
                serverToClient: true);
            if (!SessionHandshake.VerifyProof(
                    expectedServerProof,
                    serverHello.ServerProof))
            {
                throw new InvalidOperationException(
                    "The session agent proof is invalid.");
            }

            var clientProof = SessionHandshake.CreateProof(
                secret,
                clientNonce,
                serverHello.ServerNonce,
                serverHello.Binding,
                serverToClient: false);
            await SessionProtocolJson.WriteAsync(
                stream,
                new SessionClientProof(clientProof),
                timeout.Token).ConfigureAwait(false);
            var accepted = await SessionProtocolJson.ReadAsync<SessionHandshakeAccepted>(
                stream,
                timeout.Token).ConfigureAwait(false);
            if (accepted is null || !accepted.Accepted)
            {
                throw new InvalidOperationException(
                    accepted?.ErrorCode ?? "Session authentication failed.");
            }

            var connection = new SessionConnection(stream, handle);
            connection.StartReading();
            if (!_connections.TryAdd(handle.EnvironmentId, connection))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return _connections[handle.EnvironmentId].Handshake;
            }

            return connection.Handshake;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (bootstrap is not null)
                CryptographicOperations.ZeroMemory(
                    bootstrap.BootstrapSecret);
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
        var connection = GetConnection(handle);
        var response = await connection.RequestAsync(
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
        if (_bootstrapSecret is not null)
            CryptographicOperations.ZeroMemory(_bootstrapSecret);
    }

    private async Task SendControlAsync(
        SessionHandle handle,
        string kind,
        Guid commandId,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var connection = GetConnection(handle);
        var requestId = Guid.NewGuid();
        var response = await connection.RequestAsync(
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
                response.ErrorCode ?? $"Session control '{kind}' failed.");
        }
    }

    private SessionConnection GetConnection(SessionHandle handle) =>
        _connections.TryGetValue(handle.EnvironmentId, out var connection)
            ? connection
            : throw new InvalidOperationException(
                "The session agent transport is not connected.");

    private sealed class SessionConnection : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly ConcurrentDictionary<
            Guid,
            TaskCompletionSource<SessionWireEnvelope>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _readLoop;

        public SessionConnection(
            NamedPipeClientStream stream,
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

        public void StartReading()
        {
            _readLoop = ReadLoopAsync();
        }

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

                return await completion.Task.WaitAsync(cancellationToken)
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
                    new IOException("The session agent connection closed."));
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
                        await SessionProtocolJson.ReadAsync<SessionWireEnvelope>(
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
                        new IOException("The session agent connection closed."));
                }
            }
        }
    }
}
