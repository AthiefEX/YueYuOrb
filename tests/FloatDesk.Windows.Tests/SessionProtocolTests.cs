using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;
using FloatDesk.Windows.DesktopSessions;
using System.Globalization;
using System.Security.Cryptography;

namespace FloatDesk.Windows.Tests;

public sealed class SessionProtocolTests
{
    [Fact]
    public async Task WrongSessionCannotReceiveInput()
    {
        var binding = CreateBinding(sessionId: 2);
        var executor = new RecordingExecutor
        {
            CompleteImmediately = true
        };
        var dispatcher = new SessionCommandDispatcher(binding, executor);
        var command = CreateCommand(binding, sessionId: 1);

        var result = await dispatcher.DispatchAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("WrongSession", result.ErrorCode);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public void SpoofedIdentityRejected()
    {
        var binding = CreateBinding(sessionId: 2);
        var peer = new SessionPeerIdentity(
            ProcessId: 123,
            SessionId: 2,
            UserSid: binding.UserSid);
        var spoofed = new SessionClientHello(
            SessionProtocol.CurrentVersion,
            binding.EnvironmentId,
            Guid.NewGuid(),
            binding.LoginInstanceId,
            binding.WindowsSessionId,
            SessionHandshake.CreateNonce());

        Assert.Throws<InvalidOperationException>(
            () => SessionConnectionAuthorization.Validate(
                binding,
                peer,
                spoofed));
    }

    [Fact]
    public void WrongUserCannotReceiveInput()
    {
        var binding = CreateBinding(sessionId: 2);
        var peer = new SessionPeerIdentity(
            ProcessId: 123,
            SessionId: 2,
            UserSid: "S-1-5-21-999");
        var hello = ValidHello(binding);

        Assert.Throws<InvalidOperationException>(
            () => SessionConnectionAuthorization.Validate(
                binding,
                peer,
                hello));
    }

    [Fact]
    public async Task OversizedFrameRejected()
    {
        var payload = new byte[SessionProtocol.MaxFrameBytes + 1];
        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionFrameCodec.WriteAsync(
                stream,
                payload,
                CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredCommandRejected()
    {
        var binding = CreateBinding(sessionId: 2);
        var executor = new RecordingExecutor();
        var dispatcher = new SessionCommandDispatcher(binding, executor);
        var command = CreateCommand(binding, sessionId: 2) with
        {
            DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        var result = await dispatcher.DispatchAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("CommandExpired", result.ErrorCode);
        Assert.Equal(0, executor.ExecutionCount);
    }

    [Fact]
    public async Task DuplicateCommandNotExecutedTwice()
    {
        var binding = CreateBinding(sessionId: 2);
        var executor = new RecordingExecutor
        {
            CompleteImmediately = true
        };
        var dispatcher = new SessionCommandDispatcher(binding, executor);
        var command = CreateCommand(binding, sessionId: 2);

        var first = dispatcher.DispatchAsync(
            command,
            CancellationToken.None);
        var second = dispatcher.DispatchAsync(
            command,
            CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, executor.ExecutionCount);
    }

    [Fact]
    public async Task CancelReleasesHeldKeys()
    {
        var binding = CreateBinding(sessionId: 2);
        var executor = new RecordingExecutor();
        var dispatcher = new SessionCommandDispatcher(binding, executor);
        var command = CreateCommand(binding, sessionId: 2);

        var dispatch = dispatcher.DispatchAsync(
            command,
            CancellationToken.None);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.CancelAsync(command.CommandId, CancellationToken.None);
        await dispatcher.CancelAsync(command.CommandId, CancellationToken.None);
        var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.OutcomeUnknown);
        Assert.Equal(1, executor.ReleaseCount);
    }

    [Fact]
    public async Task ExecutorFailureReturnsRejectedResult()
    {
        var binding = CreateBinding(sessionId: 2);
        var dispatcher = new SessionCommandDispatcher(
            binding,
            new FailingExecutor());
        var command = CreateCommand(binding, sessionId: 2);

        var result = await dispatcher.DispatchAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("InputExecutorFailed", result.ErrorCode);
    }

    [Fact]
    public void BootstrapSecretNotInArguments()
    {
        var binding = CreateBinding(sessionId: 2);
        var arguments = ValidArguments(binding)
            .Concat(["--secret", "not-on-the-command-line"])
            .ToArray();

        Assert.Throws<ArgumentException>(
            () => SessionAgentOptions.Parse(arguments));
    }

    [Fact]
    public void AgentOptionsParseWithoutSecret()
    {
        var binding = CreateBinding(sessionId: 2);

        var options = SessionAgentOptions.Parse(
            ValidArguments(binding));

        Assert.Equal(binding, options.Binding);
        Assert.Equal("yueyu-session-test", options.PipeName);
    }

    [Fact]
    public void HandshakeUsesSixteenByteNonce()
    {
        var nonce = SessionHandshake.CreateNonce();

        Assert.Equal(16, nonce.Length);
        Assert.NotEqual(nonce, SessionHandshake.CreateNonce());
    }

    [Fact]
    public void HandshakeProofRejectsWrongBootstrapSecret()
    {
        var binding = CreateBinding(sessionId: 2);
        var secret = RandomNumberGenerator.GetBytes(
            SessionProtocol.BootstrapSecretSize);
        var wrongSecret = RandomNumberGenerator.GetBytes(
            SessionProtocol.BootstrapSecretSize);
        var clientNonce = SessionHandshake.CreateNonce();
        var serverNonce = SessionHandshake.CreateNonce();
        var expected = SessionHandshake.CreateProof(
            secret,
            clientNonce,
            serverNonce,
            binding,
            serverToClient: true);
        var forged = SessionHandshake.CreateProof(
            wrongSecret,
            clientNonce,
            serverNonce,
            binding,
            serverToClient: true);

        Assert.True(SessionHandshake.VerifyProof(expected, expected));
        Assert.False(SessionHandshake.VerifyProof(expected, forged));
    }

    [Fact]
    public async Task HostCrashExpiresLease()
    {
        var lease = new SessionInputLease();
        Assert.True(lease.Acquire(1));
        Assert.False(lease.IsExpired);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        Assert.True(lease.Expire());
        Assert.True(lease.IsExpired);
        Assert.False(lease.Renew());
    }

    private static SessionAgentBinding CreateBinding(uint sessionId) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            sessionId,
            "S-1-5-21-100");

    private static SessionClientHello ValidHello(
        SessionAgentBinding binding) =>
        new(
            SessionProtocol.CurrentVersion,
            binding.EnvironmentId,
            binding.Generation,
            binding.LoginInstanceId,
            binding.WindowsSessionId,
            SessionHandshake.CreateNonce());

    private static SessionCommand CreateCommand(
        SessionAgentBinding binding,
        uint sessionId) =>
        new(
            Guid.NewGuid(),
            new SessionHandle(
                binding.EnvironmentId,
                binding.Generation,
                sessionId,
                binding.LoginInstanceId,
                true),
            SessionCommandKind.TypeText,
            new Dictionary<string, string> { ["text"] = "hello" },
            DateTimeOffset.UtcNow.AddMinutes(1));

    private static string[] ValidArguments(
        SessionAgentBinding binding) =>
        [
            "--pipe", "yueyu-session-test",
            "--environment", binding.EnvironmentId.ToString("D"),
            "--generation", binding.Generation.ToString("D"),
            "--login", binding.LoginInstanceId.ToString("D"),
            "--session", binding.WindowsSessionId.ToString(
                CultureInfo.InvariantCulture),
            "--user-sid", binding.UserSid
        ];

    private sealed class RecordingExecutor : ISessionInputExecutor
    {
        private int _releaseCount;

        public int ExecutionCount { get; private set; }
        public int ReleaseCount => Volatile.Read(ref _releaseCount);
        public bool CompleteImmediately { get; set; }
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommandResult> ExecuteAsync(
            SessionCommand command,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            Started.TrySetResult();
            if (CompleteImmediately)
            {
                return new CommandResult(
                    command.CommandId,
                    true,
                    false,
                    null);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public Task ReleaseHeldInputAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _releaseCount);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingExecutor : ISessionInputExecutor
    {
        public Task<CommandResult> ExecuteAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("executor failed");

        public Task ReleaseHeldInputAsync(
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
