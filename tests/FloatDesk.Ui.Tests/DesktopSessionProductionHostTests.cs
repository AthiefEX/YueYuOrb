using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopSessionProductionHostTests
{
    [Fact]
    public async Task DisconnectedHyperVEnvironmentCanReauthenticateWithoutShuttingDownGuest()
    {
        var profile = CreateProfile(Guid.NewGuid()) with { Backend = DesktopSessionBackendKind.HyperV };
        var settings = AppSettings.CreateDefault() with { DesktopSessions = new(true, [profile], []) };
        var backends = new List<FakeBackend>();
        using var host = new DesktopSessionProductionHost(() => settings, _ =>
        {
            var backend = new FakeBackend();
            backends.Add(backend);
            return new(backend, new FakeTransport());
        }, new FakeStartupLedger());
        Assert.True((await host.StartEnvironmentAsync(profile, CancellationToken.None)).Success);
        backends[0].DisconnectForTest();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (host.IsEnvironmentReady(profile.Id)) await Task.Delay(10, timeout.Token);
        var reopened = await host.StartEnvironmentAsync(profile, timeout.Token);
        Assert.True(reopened.Success, reopened.Message);
        Assert.True(host.IsEnvironmentReady(profile.Id));
        Assert.Equal(2, backends.Count);
        Assert.Equal(SessionStopMode.Disconnect, backends[0].LastStopMode);
        await host.ShutdownAsync(CancellationToken.None);
    }
    [Fact]
    public async Task BackendFactoryFailureIsReportedWithoutStoppingOtherMode()
    {
        var local = CreateProfile(Guid.NewGuid());
        var vm = CreateProfile(Guid.NewGuid()) with { Backend = DesktopSessionBackendKind.HyperV };
        var settings = AppSettings.CreateDefault() with { DesktopSessions = new(true, [local, vm], []) };
        using var host = new DesktopSessionProductionHost(
            () => settings,
            profile => profile.Backend == DesktopSessionBackendKind.HyperV
                ? throw new InvalidOperationException("VmUnavailable")
                : new(new FakeBackend(), new FakeTransport()),
            new FakeStartupLedger());
        Assert.True((await host.StartEnvironmentAsync(local, CancellationToken.None)).Success);
        var failure = await host.StartEnvironmentAsync(vm, CancellationToken.None);
        Assert.False(failure.Success);
        Assert.Contains("VmUnavailable", failure.Message);
        Assert.True(host.IsEnvironmentReady(local.Id));
        await host.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LocalAndVmCanRunTogetherAndStopIndependently()
    {
        var local = CreateProfile(Guid.NewGuid());
        var vm = CreateProfile(Guid.NewGuid()) with { Backend = DesktopSessionBackendKind.HyperV };
        var settings = AppSettings.CreateDefault() with
        {
            DesktopSessions = new(true, [local, vm], [])
        };
        using var host = new DesktopSessionProductionHost(
            () => settings,
            _ => new(new FakeBackend(), new FakeTransport()),
            new FakeStartupLedger());
        Assert.True((await host.StartEnvironmentAsync(local, CancellationToken.None)).Success);
        Assert.True((await host.StartEnvironmentAsync(vm, CancellationToken.None)).Success);
        Assert.True(host.IsEnvironmentReady(local.Id));
        Assert.True(host.IsEnvironmentReady(vm.Id));
        Assert.True((await host.StopEnvironmentAsync(local.Id, CancellationToken.None)).Success);
        Assert.False(host.IsEnvironmentReady(local.Id));
        Assert.True(host.IsEnvironmentReady(vm.Id));
        await host.ShutdownAsync(CancellationToken.None);
        Assert.False(host.IsEnvironmentReady(vm.Id));
    }

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
        Assert.Equal(1, backend.ShowCalls);

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
        public int ShowCalls { get; private set; }
        public int StopCalls { get; private set; }
        public SessionStopMode? LastStopMode { get; private set; }
        private SessionHandle? _lastHandle;
        public void DisconnectForTest()
        {
            var handle = _lastHandle!;
            _events.Writer.TryWrite(new(handle.EnvironmentId, handle.Generation, DateTimeOffset.UtcNow,
                SessionBackendEventKind.Disconnected, handle.LoginInstanceId, null, null));
        }

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
            _lastHandle = new SessionHandle(
                profile.Id,
                Guid.NewGuid(),
                2,
                Guid.NewGuid(),
                true);
            return Task.FromResult(_lastHandle);
        }

        public Task ShowAsync(
            SessionHandle handle,
            CancellationToken cancellationToken)
        {
            ShowCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(
            SessionHandle handle,
            SessionStopMode mode,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            LastStopMode = mode;
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
