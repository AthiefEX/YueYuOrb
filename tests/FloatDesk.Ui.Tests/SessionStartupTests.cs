using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class SessionStartupTests
{
    [Fact]
    public async Task ReconnectDoesNotRelaunch()
    {
        var runner = new RecordingRunner();
        var coordinator = CreateCoordinator(runner);
        var handle = CreateHandle();
        var entries = new[] { CreateEntry() };

        await coordinator.RunOnceAsync(
            handle,
            entries,
            CancellationToken.None);
        await coordinator.RunOnceAsync(
            handle with { Generation = Guid.NewGuid() },
            entries,
            CancellationToken.None);

        Assert.Equal(1, runner.RunCount);
    }

    [Fact]
    public async Task NewLoginRunsOnce()
    {
        var runner = new RecordingRunner();
        var coordinator = CreateCoordinator(runner);
        var handle = CreateHandle();
        var entries = new[] { CreateEntry() };

        await coordinator.RunOnceAsync(
            handle,
            entries,
            CancellationToken.None);
        var newLogin = Guid.NewGuid();
        await coordinator.RunOnceAsync(
            handle with
            {
                Generation = Guid.NewGuid(),
                LoginInstanceId = newLogin
            },
            entries,
            CancellationToken.None);
        await coordinator.RunOnceAsync(
            handle with
            {
                Generation = Guid.NewGuid(),
                LoginInstanceId = newLogin
            },
            entries,
            CancellationToken.None);

        Assert.Equal(2, runner.RunCount);
    }

    [Fact]
    public async Task UnsupportedManagedOnlyRejected()
    {
        var runner = new RecordingRunner();
        var coordinator = new SessionStartupCoordinator(
            new MemoryLedger(),
            runner,
            new ImmediateClock(),
            SessionStartupPolicy.ManagedOnly,
            new SessionCapabilities(
                CanRunHidden: false,
                CanSuppressStartupBeforeLaunch: false,
                SupportedStartupSources: ["EnvironmentDefault"],
                UnsupportedStartupSources: ["ManagedOnly"]));

        var exception = await Assert.ThrowsAsync<SessionStartupException>(
            () => coordinator.RunOnceAsync(
                CreateHandle(),
                [CreateEntry()],
                CancellationToken.None));

        Assert.Equal("ManagedOnlyUnsupported", exception.ErrorCode);
        Assert.Equal(0, runner.RunCount);
    }

    [Fact]
    public async Task MissingGuestPathFailsClearly()
    {
        var runner = new RecordingRunner
        {
            Result = StartupRunResult.Failed("StartupPathMissing")
        };
        var coordinator = CreateCoordinator(runner);

        var exception = await Assert.ThrowsAsync<SessionStartupException>(
            () => coordinator.RunOnceAsync(
                CreateHandle(),
                [CreateEntry()],
                CancellationToken.None));

        Assert.Equal("StartupPathMissing", exception.ErrorCode);
        Assert.Contains(
            "missing.exe",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoSharedStartupMutation()
    {
        var runner = new RecordingRunner();
        var ledger = new MemoryLedger();
        var coordinator = CreateCoordinator(runner, ledger);
        var entries = new[] { CreateEntry() };
        var before = Snapshot(entries);

        await coordinator.RunOnceAsync(
            CreateHandle(),
            entries,
            CancellationToken.None);

        Assert.Equal(before, Snapshot(entries));
        Assert.Single(ledger.Keys);
    }

    [Fact]
    public async Task CleanupNeverTargetsParentSession()
    {
        var runner = new RecordingRunner();
        var coordinator = CreateCoordinator(runner);
        var handle = CreateHandle();

        await coordinator.RunOnceAsync(
            handle,
            [CreateEntry()],
            CancellationToken.None);

        Assert.All(
            runner.Handles,
            recorded => Assert.Equal(3u, recorded.WindowsSessionId));
        Assert.Equal(0, runner.CleanupCount);
    }

    private static SessionStartupCoordinator CreateCoordinator(
        RecordingRunner runner,
        MemoryLedger? ledger = null) =>
        new(
            ledger ?? new MemoryLedger(),
            runner,
            new ImmediateClock(),
            SessionStartupPolicy.EnvironmentDefault,
            new SessionCapabilities(
                CanRunHidden: false,
                CanSuppressStartupBeforeLaunch: false,
                SupportedStartupSources: ["EnvironmentDefault"],
                UnsupportedStartupSources: []));

    private static SessionHandle CreateHandle() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        WindowsSessionId: 3,
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        PreparationCompleted: true);

    private static SessionStartupEntry CreateEntry() => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "记事本",
        @"C:\missing.exe",
        ["--fixed"],
        WorkingDirectory: null,
        Order: 0,
        DelaySeconds: 0,
        Enabled: true,
        WaitForExit: false,
        Revision: 1);

    private static string Snapshot(
        IReadOnlyList<SessionStartupEntry> entries) =>
        System.Text.Json.JsonSerializer.Serialize(entries);

    private sealed class MemoryLedger : IStartupExecutionLedger
    {
        public HashSet<StartupExecutionKey> Keys { get; } = [];

        public Task<bool> TryBeginAsync(
            StartupExecutionKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Keys.Add(key));
        }
    }

    private sealed class RecordingRunner : IStartupApplicationRunner
    {
        public StartupRunResult Result { get; set; } =
            StartupRunResult.Success;
        public int RunCount { get; private set; }
        public int CleanupCount { get; private set; }
        public List<SessionHandle> Handles { get; } = [];

        public Task<StartupRunResult> RunAsync(
            SessionHandle handle,
            SessionStartupEntry entry,
            CancellationToken cancellationToken)
        {
            RunCount++;
            Handles.Add(handle);
            return Task.FromResult(Result);
        }

        public Task CleanupOwnedProcessesAsync(
            SessionHandle handle,
            CancellationToken cancellationToken)
        {
            CleanupCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
