using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;
using System.Collections.Concurrent;

namespace FloatDesk.Windows.Tests;

public sealed class AutomationStepExecutorTests
{
    [Fact(Timeout = 5_000)]
    public async Task CancelKillsOnlyOwnedScriptTree()
    {
        using var scripts = new BlockingScriptRunner();
        var automation = new RecordingAutomation();
        var executor = new AutomationStepExecutor(
            automation,
            scripts,
            new ImmediateClock());
        var command = CreateCommand(
            SessionCommandKind.RunScript,
            new Dictionary<string, string>
            {
                ["scriptPath"] = @"C:\tools\owned.ps1",
                ["arguments"] = "[]"
            });
        var execution = executor.ExecuteAsync(
            command,
            CancellationToken.None);
        await scripts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await executor.ReleaseHeldInputAsync(CancellationToken.None);

        Assert.Equal(1, scripts.ReleaseCount);
        Assert.Equal([101], scripts.TerminatedProcessIds);
        Assert.DoesNotContain(1, scripts.TerminatedProcessIds);
        Assert.Equal(1, automation.ReleaseCount);
        scripts.CompleteCancellation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution);
    }

    [Fact]
    public async Task DelayUsesGuestClock()
    {
        var clock = new ImmediateClock();
        var executor = new AutomationStepExecutor(
            new RecordingAutomation(),
            new BlockingScriptRunner(),
            clock);
        var command = CreateCommand(
            SessionCommandKind.Delay,
            new Dictionary<string, string>
            {
                ["milliseconds"] = "125"
            });

        var result = await executor.ExecuteAsync(
            command,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            TimeSpan.FromMilliseconds(125),
            clock.LastDelay);
    }

    [Fact]
    public async Task InputReleaseFailureStillTerminatesOwnedScripts()
    {
        using var scripts = new BlockingScriptRunner();
        var automation = new RecordingAutomation
        {
            ThrowOnRelease = true
        };
        var executor = new AutomationStepExecutor(
            automation,
            scripts,
            new ImmediateClock());

        await Assert.ThrowsAnyAsync<Exception>(
            () => executor.ReleaseHeldInputAsync(
                CancellationToken.None));

        Assert.Equal(1, automation.ReleaseCount);
        Assert.Equal(1, scripts.ReleaseCount);
    }

    private static SessionCommand CreateCommand(
        SessionCommandKind kind,
        IReadOnlyDictionary<string, string> parameters) =>
        new(
            Guid.NewGuid(),
            new SessionHandle(
                Guid.NewGuid(),
                Guid.NewGuid(),
                WindowsSessionId: 3,
                Guid.NewGuid(),
                PreparationCompleted: true),
            kind,
            parameters,
            DateTimeOffset.UtcNow.AddMinutes(1));

    private sealed class RecordingAutomation :
        ISessionAutomationBackend
    {
        public int ReleaseCount { get; private set; }
        public bool ThrowOnRelease { get; init; }

        public Task<CommandResult> WaitForWindowAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Success(command);

        public Task<CommandResult> ClickElementAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Success(command);

        public Task<CommandResult> TypeTextAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Success(command);

        public Task<CommandResult> SendKeysAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Success(command);

        public Task<CommandResult> CaptureScreenshotAsync(
            SessionCommand command,
            CancellationToken cancellationToken) =>
            Success(command);

        public Task ReleaseHeldInputAsync(
            CancellationToken cancellationToken)
        {
            ReleaseCount++;
            if (ThrowOnRelease)
                throw new InvalidOperationException("release failed");
            return Task.CompletedTask;
        }

        private static Task<CommandResult> Success(
            SessionCommand command) =>
            Task.FromResult(new CommandResult(
                command.CommandId,
                true,
                false,
                null));
    }

    private sealed class BlockingScriptRunner :
        ISessionScriptRunner,
        IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseCount { get; private set; }
        public ConcurrentQueue<int> TerminatedProcessIds { get; } = [];

        public async Task<CommandResult> RunAsync(
            SessionCommand command,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken,
                    _cancellation.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return new CommandResult(
                command.CommandId,
                true,
                false,
                null);
        }

        public Task ReleaseOwnedProcessesAsync(
            CancellationToken cancellationToken)
        {
            ReleaseCount++;
            TerminatedProcessIds.Enqueue(101);
            _cancellation.Cancel();
            return Task.CompletedTask;
        }

        public void CompleteCancellation() => _cancellation.Cancel();

        public void Dispose() => _cancellation.Dispose();
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public TimeSpan LastDelay { get; private set; }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            LastDelay = delay;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
