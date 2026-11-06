using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopSessionProductionHostTests
{
    [Fact]
    public async Task StartsRunsTaskAndStopsSingleProductionEnvironment()
    {
        var environmentId = Guid.NewGuid();
        var profile = CreateProfile(environmentId);
        var settings = AppSettings.CreateDefault() with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [profile],
                [])
        };
        var backend = new FakeBackend();
        var transport = new FakeTransport();
        using var host = new DesktopSessionProductionHost(
            () => settings,
            _ => new DesktopSessionBackendBundle(backend, transport),
            new FakeStartupLedger());

        var start = await host.StartEnvironmentAsync(
            profile,
            CancellationToken.None);

        Assert.True(start.Success);
        Assert.True(host.IsEnvironmentReady(environmentId));
        Assert.Equal(SessionRuntimeStatus.Ready, host.GetStatus(environmentId));
        Assert.Equal(1, backend.StartCalls);

        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "测试任务",
            environmentId,
            [
                new SessionTaskStep(
                    Guid.NewGuid(),
                    SessionCommandKind.Delay,
                    new Dictionary<string, string>
                    {
                        ["milliseconds"] = "1"
                    },
                    TimeoutSeconds: 10,
                    RetryCount: 0)
            ],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, "UTC"),
            Revision: 1,
            Enabled: true);
        var result = await host.RunTaskAsync(
            task,
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(DesktopTaskRunOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, transport.SendCalls);
        Assert.Equal(1, transport.LeaseAcquireCalls);
        Assert.Equal(1, transport.LeaseReleaseCalls);
        Assert.True((await host.StopEnvironmentAsync(
            environmentId,
            CancellationToken.None)).Success);
        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(SessionRuntimeStatus.Stopped, host.GetStatus(environmentId));
    }

    [Fact]
    public async Task RefusesSecondEnvironmentWhileOneIsActive()
    {
        var first = CreateProfile(Guid.NewGuid());
        var second = CreateProfile(Guid.NewGuid());
        var settings = AppSettings.CreateDefault() with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [first, second],
                [])
        };
        using var host = new DesktopSessionProductionHost(
            () => settings,
            _ => new DesktopSessionBackendBundle(
                new FakeBackend(),
                new FakeTransport()),
            new FakeStartupLedger());

        Assert.True((await host.StartEnvironmentAsync(
            first,
            CancellationToken.None)).Success);
        var result = await host.StartEnvironmentAsync(
            second,
            CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("仅允许一个", result.Message, StringComparison.Ordinal);
    }

    private static EnvironmentProfile CreateProfile(Guid id) =>
        new(
            id,
            "测试环境",
            DesktopSessionBackendKind.LocalChild,
            "local",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);

    private sealed class FakeStartupLedger : IStartupExecutionLedger
    {
        public Task<bool> TryBeginAsync(
            StartupExecutionKey key,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeBackend : IDesktopSessionBackend
    {
        private readonly Channel<SessionBackendEvent> _events =
            Channel.CreateUnbounded<SessionBackendEvent>();
        private readonly SessionCapabilities _capabilities = new(
            true,
            false,
            ["EnvironmentDefault"],
            []);

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public Task<SessionCapabilities> ProbeAsync(
            EnvironmentProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult(_capabilities);

        public Task<PreparationResult> PrepareAsync(
            EnvironmentProfile profile,
            SessionStartupPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PreparationResult(
                true,
                false,
                _capabilities,
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

        public Task StopAsync(
            SessionHandle handle,
            SessionStopMode mode,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            _events.Writer.TryWrite(new SessionBackendEvent(
                handle.EnvironmentId,
                handle.Generation,
                DateTimeOffset.UtcNow,
                SessionBackendEventKind.Disconnected,
                handle.LoginInstanceId,
                null,
                null));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<SessionBackendEvent> WatchAsync(
            Guid environmentId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var value in _events.Reader.ReadAllAsync(
                               cancellationToken))
            {
                yield return value;
            }
        }
    }

    private sealed class FakeTransport : IDesktopSessionTransport
    {
        public int SendCalls { get; private set; }
        public int LeaseAcquireCalls { get; private set; }
        public int LeaseReleaseCalls { get; private set; }

        public Task<SessionTransportHandshake> ConnectAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionTransportHandshake(
                true,
                handle.Generation,
                handle.LoginInstanceId,
                null));

        public Task AcquireInputLeaseAsync(
            SessionHandle handle,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken)
        {
            LeaseAcquireCalls++;
            return Task.CompletedTask;
        }

        public Task RenewInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReleaseInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken)
        {
            LeaseReleaseCalls++;
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            SessionHandle handle,
            Guid commandId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<CommandResult> SendAsync(
            SessionHandle handle,
            SessionCommand command,
            CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.FromResult(new CommandResult(
                command.CommandId,
                true,
                false,
                null));
        }
    }
}
