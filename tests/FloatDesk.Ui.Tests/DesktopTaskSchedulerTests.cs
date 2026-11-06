using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopTaskSchedulerTests
{
    private static readonly Guid EnvironmentId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task ScheduleMissedWhileUnavailableIsSkipped()
    {
        var context = new RecordingContext
        {
            Ready = false
        };
        var scheduler = new DesktopTaskScheduler(
            new MemoryLedger(),
            context);
        var task = CreateTask(
            TaskTriggerKind.Scheduled,
            localTime: "09:30",
            timeZoneId: "UTC");
        var dueFrom = new DateTimeOffset(
            2026,
            6,
            1,
            9,
            29,
            0,
            TimeSpan.Zero);
        var dueTo = dueFrom.AddMinutes(2);

        var skipped = await scheduler.ProcessScheduledAsync(
            [task],
            dueFrom,
            dueTo,
            CancellationToken.None);
        context.Ready = true;
        var late = await scheduler.ProcessScheduledAsync(
            [task],
            dueFrom,
            dueTo,
            CancellationToken.None);

        Assert.Equal(0, skipped);
        Assert.Equal(0, late);
        Assert.Empty(context.Runs);
    }

    [Fact]
    public async Task DstDuplicateTimeRunsOnce()
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(
                "Pacific Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        var context = new RecordingContext
        {
            Ready = true
        };
        var scheduler = new DesktopTaskScheduler(
            new MemoryLedger(),
            context);
        var task = CreateTask(
            TaskTriggerKind.Scheduled,
            localTime: "01:30",
            timeZoneId: timeZone.Id);
        var fromUtc = new DateTimeOffset(
            2026,
            11,
            1,
            7,
            0,
            0,
            TimeSpan.Zero);
        var toUtc = fromUtc.AddHours(4);

        var started = await scheduler.ProcessScheduledAsync(
            [task],
            fromUtc,
            toUtc,
            CancellationToken.None);

        Assert.Equal(1, started);
        Assert.Single(context.Runs);
    }

    [Fact]
    public async Task ReconnectDoesNotTriggerReadyTaskAgain()
    {
        var loginInstanceId = Guid.NewGuid();
        var context = new RecordingContext
        {
            Ready = true,
            LoginInstanceId = loginInstanceId
        };
        var scheduler = new DesktopTaskScheduler(
            new MemoryLedger(),
            context);
        var task = CreateTask(
            TaskTriggerKind.EnvironmentReady,
            localTime: null,
            timeZoneId: null);

        var first = await scheduler.ProcessEnvironmentReadyAsync(
            task,
            CancellationToken.None);
        var reconnect = await scheduler.ProcessEnvironmentReadyAsync(
            task,
            CancellationToken.None);
        context.LoginInstanceId = Guid.NewGuid();
        var newLogin = await scheduler.ProcessEnvironmentReadyAsync(
            task,
            CancellationToken.None);

        Assert.True(first);
        Assert.False(reconnect);
        Assert.True(newLogin);
        Assert.Equal(2, context.Runs.Count);
    }

    private static DesktopSessionTask CreateTask(
        TaskTriggerKind trigger,
        string? localTime,
        string? timeZoneId) =>
        new(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "测试任务",
            EnvironmentId,
            [
                new SessionTaskStep(
                    Guid.NewGuid(),
                    SessionCommandKind.Delay,
                    new Dictionary<string, string>
                    {
                        ["milliseconds"] = "1"
                    },
                    TimeoutSeconds: 5,
                    RetryCount: 0)
            ],
            new SessionTaskTrigger(trigger, localTime, timeZoneId),
            Revision: 1,
            Enabled: true);

    private sealed class MemoryLedger : IDesktopTaskExecutionLedger
    {
        private readonly HashSet<string> _keys = [];

        public Task<bool> TryBeginAsync(
            string key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_keys.Add(key));
        }
    }

    private sealed class RecordingContext : IDesktopTaskScheduleContext
    {
        public bool Ready { get; set; }
        public Guid? LoginInstanceId { get; set; }
        public List<Guid> Runs { get; } = [];

        public bool IsEnvironmentReady(Guid environmentId) => Ready;

        public Guid? GetLoginInstanceId(Guid environmentId) =>
            LoginInstanceId;

        public Task<DesktopTaskRunResult> RunAsync(
            DesktopSessionTask task,
            Guid runId,
            CancellationToken cancellationToken)
        {
            Runs.Add(runId);
            return Task.FromResult(new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Succeeded,
                1,
                null,
                null));
        }
    }
}
