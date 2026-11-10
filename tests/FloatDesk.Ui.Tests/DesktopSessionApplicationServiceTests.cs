using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopSessionApplicationServiceTests
{
    [Fact]
    public async Task DisabledLocalModeBlocksStartAndTasksWhileVmEnabled()
    {
        var settings = CreateSettings(enabled: true);
        settings = settings with
        {
            DesktopSessions = settings.DesktopSessions with
            {
                LocalChild = new(false, null), HyperV = new(true, null)
            }
        };
        var host = new RecordingHost();
        using var service = new DesktopSessionApplicationService(() => settings, new MemoryLedger(), host);
        Assert.False((await service.StartEnvironmentAsync(settings.DesktopSessions.Environments[0].Id, CancellationToken.None)).Success);
        Assert.False((await service.RunTaskAsync(settings.DesktopSessions.Tasks[0].Id, CancellationToken.None)).Success);
        Assert.False(service.IsEnvironmentReady(settings.DesktopSessions.Environments[0].Id));
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.RunCalls);
    }

    [Fact]
    public async Task DisabledFeatureDoesNotInvokeHost()
    {
        var settings = CreateSettings(enabled: false);
        var host = new RecordingHost();
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger(),
            host);

        var start = await service.StartEnvironmentAsync(
            settings.DesktopSessions.Environments[0].Id,
            CancellationToken.None);
        var run = await service.RunTaskAsync(
            settings.DesktopSessions.Tasks[0].Id,
            CancellationToken.None);

        Assert.False(start.Success);
        Assert.False(run.Success);
        Assert.Equal(0, host.StartCalls);
        Assert.Equal(0, host.RunCalls);
    }

    [Fact]
    public async Task MissingRuntimeNeverFallsBackToMainDesktop()
    {
        var settings = CreateSettings(enabled: true);
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger());

        var result = await service.RunTaskAsync(
            settings.DesktopSessions.Tasks[0].Id,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("未准备", result.Message);
        Assert.DoesNotContain("主桌面执行成功", result.Message);
    }

    [Fact]
    public async Task ManualRunUsesHostAndRecordsActivity()
    {
        var settings = CreateSettings(enabled: true);
        var task = settings.DesktopSessions.Tasks[0];
        var host = new RecordingHost
        {
            RunResult = new DesktopTaskRunResult(
                Guid.NewGuid(),
                task.Id,
                DesktopTaskRunOutcome.Succeeded,
                CompletedSteps: task.Steps.Count,
                FailedStepId: null,
                ErrorCode: null)
        };
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger(),
            host);

        var result = await service.RunTaskAsync(
            task.Id,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, host.RunCalls);
        Assert.Contains(
            service.GetRecentActivity(),
            item => item.Contains(task.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ControlRunRejectsMismatchedEnvironment()
    {
        var settings = CreateSettings(enabled: true);
        var task = settings.DesktopSessions.Tasks[0];
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger(),
            new RecordingHost());

        var response = await service.HandleAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.RunOperation,
                Guid.NewGuid(),
                task.Id,
                RunId: null),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("TaskNotFound", response.ErrorCode);
    }

    [Fact]
    public async Task ControlRunRejectsDisabledFeatureWithoutInvokingHost()
    {
        var enabled = CreateSettings(enabled: true);
        var settings = enabled with
        {
            DesktopSessions = enabled.DesktopSessions with
            {
                Enabled = false
            }
        };
        var task = settings.DesktopSessions.Tasks[0];
        var host = new RecordingHost();
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger(),
            host);

        var response = await service.HandleAsync(
            RunRequest(task, task.EnvironmentId),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("FeatureDisabled", response.ErrorCode);
        Assert.Equal(0, host.RunCalls);
    }

    [Fact]
    public async Task ControlRunRejectsDisabledTaskWithoutInvokingHost()
    {
        var enabled = CreateSettings(enabled: true);
        var task = enabled.DesktopSessions.Tasks[0] with
        {
            Enabled = false
        };
        var settings = enabled with
        {
            DesktopSessions = enabled.DesktopSessions with
            {
                Tasks = [task]
            }
        };
        var host = new RecordingHost();
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger(),
            host);

        var response = await service.HandleAsync(
            RunRequest(task, task.EnvironmentId),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("TaskDisabled", response.ErrorCode);
        Assert.Equal(0, host.RunCalls);
    }

    [Fact]
    public async Task ControlRunReportsUnavailableRuntime()
    {
        var settings = CreateSettings(enabled: true);
        var task = settings.DesktopSessions.Tasks[0];
        using var service = new DesktopSessionApplicationService(
            () => settings,
            new MemoryLedger());

        var response = await service.HandleAsync(
            RunRequest(task, task.EnvironmentId),
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Equal("Unavailable", response.ErrorCode);
        Assert.Contains("未准备", response.Message);
        Assert.NotEqual("TaskAlreadyRunning", response.ErrorCode);
    }

    private static DesktopSessionControlRequest RunRequest(
        DesktopSessionTask task,
        Guid environmentId) =>
        new(
            DesktopSessionControlProtocol.CurrentVersion,
            DesktopSessionControlProtocol.RunOperation,
            environmentId,
            task.Id,
            RunId: null);

    private static AppSettings CreateSettings(bool enabled)
    {
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "测试环境",
            DesktopSessionBackendKind.LocalChild,
            "local",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "测试任务",
            environment.Id,
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
            new SessionTaskTrigger(
                TaskTriggerKind.Manual,
                LocalTime: null,
                TimeZoneId: "UTC"),
            Revision: 1,
            Enabled: true);
        return AppSettings.CreateDefault() with
        {
            DesktopSessions = new DesktopSessionSettings(
                enabled,
                [environment],
                [task])
        };
    }

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

    private sealed class RecordingHost : IDesktopSessionExecutionHost
    {
        public int StartCalls { get; private set; }
        public int RunCalls { get; private set; }
        public DesktopTaskRunResult? RunResult { get; set; }

        public bool IsEnvironmentReady(Guid environmentId) => true;

        public Guid? GetLoginInstanceId(Guid environmentId) =>
            Guid.NewGuid();

        public SessionRuntimeStatus GetStatus(Guid environmentId) =>
            SessionRuntimeStatus.Ready;

        public Task<DesktopSessionActionStatus> StartEnvironmentAsync(
            EnvironmentProfile environment,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(
                new DesktopSessionActionStatus(true, "started"));
        }

        public Task<DesktopSessionActionStatus> StopEnvironmentAsync(
            Guid environmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new DesktopSessionActionStatus(true, "stopped"));

        public Task<DesktopTaskRunResult> RunTaskAsync(
            DesktopSessionTask task,
            Guid runId,
            CancellationToken cancellationToken)
        {
            RunCalls++;
            return Task.FromResult(
                RunResult ?? new DesktopTaskRunResult(
                    runId,
                    task.Id,
                    DesktopTaskRunOutcome.Succeeded,
                    task.Steps.Count,
                    FailedStepId: null,
                    ErrorCode: null));
        }

        public Task<bool> CancelRunAsync(
            Guid runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
