using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopSessionRuntimeTests
{
    [Fact]
    public async Task StaleGenerationRejected()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var current = await runtime.StartAsync(environmentId, CancellationToken.None);
        var stale = current with { Generation = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ExecuteAsync(
                stale,
                CreateCommand(stale),
                CancellationToken.None));

        Assert.Equal(0, transport.SendCalls);
    }

    [Fact]
    public async Task DisconnectMarksUnknownOutcomeWithoutReplay()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendHandler = async (_, command, _) =>
        {
            sendEntered.TrySetResult();
            await finishSend.Task;
            return new CommandResult(command.CommandId, true, false, null);
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var handle = await runtime.StartAsync(environmentId, CancellationToken.None);

        var execution = runtime.ExecuteAsync(
            handle,
            CreateCommand(handle),
            CancellationToken.None);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.Emit(new SessionBackendEvent(
            environmentId,
            handle.Generation,
            DateTimeOffset.UtcNow,
            SessionBackendEventKind.Disconnected,
            handle.LoginInstanceId,
            null,
            null));
        await WaitForStatusAsync(
            runtime,
            environmentId,
            SessionRuntimeStatus.Reconnecting);
        finishSend.TrySetResult(true);

        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.OutcomeUnknown);
        Assert.Equal(1, transport.SendCalls);
        Assert.Equal(SessionRuntimeStatus.Reconnecting, runtime.GetStatus(environmentId));
    }

    [Fact]
    public async Task ManualControlWaitsForInputRelease()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendHandler = async (_, command, _) =>
        {
            sendEntered.TrySetResult();
            await finishSend.Task;
            return new CommandResult(command.CommandId, true, false, null);
        };
        transport.ReleaseHandler = async (_, _) =>
        {
            releaseEntered.TrySetResult();
            await allowRelease.Task;
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var handle = await runtime.StartAsync(environmentId, CancellationToken.None);
        var execution = runtime.ExecuteAsync(
            handle,
            CreateCommand(handle),
            CancellationToken.None);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var takeover = runtime.TakeManualControlAsync(
            environmentId,
            CancellationToken.None);
        await releaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(takeover.IsCompleted);

        allowRelease.TrySetResult(true);
        await takeover.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionRuntimeStatus.Paused, runtime.GetStatus(environmentId));

        finishSend.TrySetResult(true);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OutcomeUnknown);
    }

    [Fact]
    public async Task ControlCancelBypassesBlockedCommand()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelCalled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendHandler = async (_, command, _) =>
        {
            sendEntered.TrySetResult();
            await finishSend.Task;
            return new CommandResult(command.CommandId, true, false, null);
        };
        transport.CancelHandler = (_, _, _) =>
        {
            cancelCalled.TrySetResult();
            return Task.CompletedTask;
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var handle = await runtime.StartAsync(environmentId, CancellationToken.None);
        var execution = runtime.ExecuteAsync(
            handle,
            CreateCommand(handle),
            CancellationToken.None);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await runtime.TakeManualControlAsync(
            environmentId,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        await cancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, transport.CancelCalls);
        Assert.Equal(SessionRuntimeStatus.Paused, runtime.GetStatus(environmentId));

        finishSend.TrySetResult(true);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OutcomeUnknown);
    }

    [Fact]
    public async Task UnkillableCommandBlocksTakeover()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var sendEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendHandler = async (_, command, _) =>
        {
            sendEntered.TrySetResult();
            await finishSend.Task;
            return new CommandResult(command.CommandId, true, false, null);
        };
        transport.ReleaseHandler = (_, _) =>
            throw new InvalidOperationException("input release was not confirmed");
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var handle = await runtime.StartAsync(environmentId, CancellationToken.None);
        var execution = runtime.ExecuteAsync(
            handle,
            CreateCommand(handle),
            CancellationToken.None);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.TakeManualControlAsync(
                environmentId,
                CancellationToken.None));

        Assert.Equal(SessionRuntimeStatus.Faulted, runtime.GetStatus(environmentId));
        finishSend.TrySetResult(true);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.OutcomeUnknown);
    }

    [Fact]
    public async Task QueueLimit20()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var finishSend = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SendHandler = async (_, command, _) =>
        {
            await finishSend.Task;
            return new CommandResult(command.CommandId, true, false, null);
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        var handle = await runtime.StartAsync(environmentId, CancellationToken.None);

        // One command runs while the remaining 20 wait in the bounded queue.
        var accepted = Enumerable.Range(0, 21)
            .Select(_ => runtime.ExecuteAsync(
                handle,
                CreateCommand(handle),
                CancellationToken.None))
            .ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ExecuteAsync(
                handle,
                CreateCommand(handle),
                CancellationToken.None));

        finishSend.TrySetResult(true);
        await Task.WhenAll(accepted).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(accepted, task => Assert.True(task.Result.Succeeded));
    }

    [Fact]
    public async Task StartCancellationCleansOnlyOwnedResources()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        var connectEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ConnectHandler = async (_, cancellationToken) =>
        {
            connectEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        using var cancellation = new CancellationTokenSource();

        var start = runtime.StartAsync(environmentId, cancellation.Token);
        await connectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(SessionStopMode.LogoffOwnedSession, backend.LastStopMode);
        Assert.Equal(SessionRuntimeStatus.Stopped, runtime.GetStatus(environmentId));
    }

    [Fact]
    public async Task FaultedSessionWithRetainedHandleMustBeStoppedBeforeRestart()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport
        {
            ReleaseHandler = (_, _) =>
                throw new InvalidOperationException("input release was not confirmed")
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);
        await runtime.StartAsync(environmentId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.TakeManualControlAsync(
                environmentId,
                CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(
                environmentId,
                CancellationToken.None));

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(SessionRuntimeStatus.Faulted, runtime.GetStatus(environmentId));
    }

    [Fact]
    public async Task MismatchedHandshakeRejectsAndCleansUpOwnedSession()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend();
        var transport = new FakeTransport
        {
            ConnectHandler = (handle, _) => Task.FromResult(
                new SessionTransportHandshake(
                    true,
                    Guid.NewGuid(),
                    handle.LoginInstanceId,
                    null))
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(
                environmentId,
                CancellationToken.None));

        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(SessionRuntimeStatus.Stopped, runtime.GetStatus(environmentId));
    }

    [Fact]
    public async Task FailedStartCleanupRetainsOwnedHandleForStopRetry()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend
        {
            StopHandler = (_, _, _) =>
                throw new InvalidOperationException("cleanup failed")
        };
        var transport = new FakeTransport
        {
            ConnectHandler = (handle, _) => Task.FromResult(
                new SessionTransportHandshake(
                    true,
                    Guid.NewGuid(),
                    handle.LoginInstanceId,
                    null))
        };
        using var runtime = CreateRuntime(environmentId, backend, transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StartAsync(
                environmentId,
                CancellationToken.None));
        Assert.Equal(SessionRuntimeStatus.Faulted, runtime.GetStatus(environmentId));
        Assert.Equal(1, backend.StopCalls);

        backend.StopHandler = null;
        await runtime.StopAsync(environmentId, CancellationToken.None);

        Assert.Equal(2, backend.StopCalls);
        Assert.Equal(SessionRuntimeStatus.Stopped, runtime.GetStatus(environmentId));
    }

    [Fact]
    public async Task FailedStopRetainsOwnedHandleForRetry()
    {
        var environmentId = Guid.NewGuid();
        var backend = new FakeBackend
        {
            StopHandler = (_, _, _) =>
                throw new InvalidOperationException("cleanup failed")
        };
        var transport = new FakeTransport();
        using var runtime = CreateRuntime(environmentId, backend, transport);
        await runtime.StartAsync(environmentId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.StopAsync(
                environmentId,
                CancellationToken.None));
        Assert.Equal(SessionRuntimeStatus.Faulted, runtime.GetStatus(environmentId));
        Assert.Equal(1, backend.StopCalls);

        backend.StopHandler = null;
        await runtime.StopAsync(environmentId, CancellationToken.None);

        Assert.Equal(2, backend.StopCalls);
        Assert.Equal(SessionRuntimeStatus.Stopped, runtime.GetStatus(environmentId));
    }

    private static DesktopSessionRuntime CreateRuntime(
        Guid environmentId,
        FakeBackend backend,
        FakeTransport transport)
    {
        var profile = new EnvironmentProfile(
            environmentId,
            "测试环境",
            DesktopSessionBackendKind.LocalChild,
            "local",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(MaxQueue: 20),
            []);
        return new DesktopSessionRuntime(backend, transport, [profile]);
    }

    private static SessionCommand CreateCommand(SessionHandle handle) =>
        new(
            Guid.NewGuid(),
            handle,
            SessionCommandKind.TypeText,
            new Dictionary<string, string> { ["text"] = "hello" },
            DateTimeOffset.UtcNow.AddMinutes(1));

    private static async Task WaitForStatusAsync(
        DesktopSessionRuntime runtime,
        Guid environmentId,
        SessionRuntimeStatus expected)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (runtime.GetStatus(environmentId) == expected)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"Expected desktop session status {expected}.");
    }

    private sealed class FakeBackend : IDesktopSessionBackend
    {
        private readonly Channel<SessionBackendEvent> _events =
            Channel.CreateUnbounded<SessionBackendEvent>();

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public SessionStopMode? LastStopMode { get; private set; }
        public Func<SessionHandle, SessionStopMode, CancellationToken, Task>?
            StopHandler { get; set; }
        public SessionCapabilities Capabilities { get; } = new(
            true,
            false,
            [],
            []);

        public Task<SessionCapabilities> ProbeAsync(
            EnvironmentProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);

        public Task<PreparationResult> PrepareAsync(
            EnvironmentProfile profile,
            SessionStartupPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PreparationResult(
                true,
                false,
                Capabilities,
                null));

        public Task<SessionHandle> StartAsync(
            EnvironmentProfile profile,
            PreparationResult preparation,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(new SessionHandle(
                profile.Id,
                Guid.NewGuid(),
                2,
                Guid.NewGuid(),
                true));
        }

        public Task ShowAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task StopAsync(
            SessionHandle handle,
            SessionStopMode mode,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            LastStopMode = mode;
            if (StopHandler is not null)
                await StopHandler(handle, mode, cancellationToken);
        }

        public async IAsyncEnumerable<SessionBackendEvent> WatchAsync(
            Guid environmentId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var backendEvent in _events.Reader.ReadAllAsync(
                               cancellationToken))
            {
                yield return backendEvent;
            }
        }

        public void Emit(SessionBackendEvent backendEvent) =>
            _events.Writer.TryWrite(backendEvent);
    }

    private sealed class FakeTransport : IDesktopSessionTransport
    {
        public int SendCalls { get; private set; }
        public int CancelCalls { get; private set; }

        public Func<SessionHandle, CancellationToken, Task<SessionTransportHandshake>>?
            ConnectHandler { get; set; }

        public Func<SessionHandle, SessionCommand, CancellationToken, Task<CommandResult>>?
            SendHandler { get; set; }

        public Func<SessionHandle, Guid, CancellationToken, Task>?
            CancelHandler { get; set; }

        public Func<SessionHandle, CancellationToken, Task>?
            ReleaseHandler { get; set; }

        public async Task<SessionTransportHandshake> ConnectAsync(
            SessionHandle handle,
            CancellationToken cancellationToken)
        {
            if (ConnectHandler is not null)
                return await ConnectHandler(handle, cancellationToken);
            return new SessionTransportHandshake(
                true,
                handle.Generation,
                handle.LoginInstanceId,
                null);
        }

        public Task AcquireInputLeaseAsync(
            SessionHandle handle,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RenewInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async Task ReleaseInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken)
        {
            if (ReleaseHandler is not null)
                await ReleaseHandler(handle, cancellationToken);
        }

        public async Task CancelAsync(
            SessionHandle handle,
            Guid commandId,
            CancellationToken cancellationToken)
        {
            CancelCalls++;
            if (CancelHandler is not null)
                await CancelHandler(handle, commandId, cancellationToken);
        }

        public async Task<CommandResult> SendAsync(
            SessionHandle handle,
            SessionCommand command,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            if (SendHandler is not null)
                return await SendHandler(handle, command, cancellationToken);
            return new CommandResult(command.CommandId, true, false, null);
        }
    }
}
