using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

public interface IDesktopSessionExecutionHost
{
    bool IsEnvironmentReady(Guid environmentId);

    Guid? GetLoginInstanceId(Guid environmentId);

    SessionRuntimeStatus GetStatus(Guid environmentId);

    Task<DesktopSessionActionStatus> StartEnvironmentAsync(
        EnvironmentProfile environment,
        CancellationToken cancellationToken);

    Task<DesktopSessionActionStatus> StopEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken);

    Task<DesktopTaskRunResult> RunTaskAsync(
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken);

    Task<bool> CancelRunAsync(
        Guid runId,
        CancellationToken cancellationToken);
}

public sealed class DesktopSessionApplicationService :
    IDesktopSessionSettingsActions,
    IDesktopTaskScheduleContext,
    IDesktopSessionControlHandler,
    IDisposable
{
    private const int MaximumActivityEntries = 50;
    private readonly Func<AppSettings> _getSettings;
    private readonly IDesktopSessionExecutionHost? _host;
    private readonly DesktopTaskScheduler _scheduler;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly object _activitySync = new();
    private readonly Queue<string> _activity = new();
    private readonly Dictionary<Guid, Guid> _activeRuns = [];
    private DateTimeOffset? _lastScheduleCheck;
    private bool _disposed;

    public DesktopSessionApplicationService(
        Func<AppSettings> getSettings,
        IDesktopTaskExecutionLedger ledger,
        IDesktopSessionExecutionHost? host = null,
        TimeProvider? timeProvider = null)
    {
        _getSettings = getSettings ??
            throw new ArgumentNullException(nameof(getSettings));
        ArgumentNullException.ThrowIfNull(ledger);
        _host = host;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _scheduler = new DesktopTaskScheduler(ledger, this);
    }

    public bool IsAvailable => _host is not null;

    public async Task<DesktopSessionActionStatus> StartEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = _getSettings();
        if (!settings.DesktopSessions.Enabled)
            return Disabled();
        var environment = FindEnvironment(settings, environmentId);
        if (environment is null)
            return new(false, "未找到桌面分身环境。");
        if (_host is null)
            return Unavailable();

        try
        {
            var status = await _host.StartEnvironmentAsync(
                environment,
                cancellationToken).ConfigureAwait(false);
            AddActivity(
                status.Success
                    ? $"已启动环境：{environment.Name}"
                    : $"环境启动失败：{environment.Name}（{status.Message}）");
            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddActivity(
                $"环境启动异常：{environment.Name}（{exception.GetType().Name}）");
            return new(false, $"环境启动失败：{exception.Message}");
        }
    }

    public async Task<DesktopSessionActionStatus> StopEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var environment = FindEnvironment(
            _getSettings(),
            environmentId);
        if (environment is null)
            return new(false, "未找到桌面分身环境。");
        if (_host is null)
            return Unavailable();

        try
        {
            var status = await _host.StopEnvironmentAsync(
                environmentId,
                cancellationToken).ConfigureAwait(false);
            AddActivity(
                status.Success
                    ? $"已停止环境：{environment.Name}"
                    : $"环境停止失败：{environment.Name}（{status.Message}）");
            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddActivity(
                $"环境停止异常：{environment.Name}（{exception.GetType().Name}）");
            return new(false, $"环境停止失败：{exception.Message}");
        }
    }

    public async Task<DesktopSessionActionStatus> RunTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = _getSettings();
        if (!settings.DesktopSessions.Enabled)
            return Disabled();
        if (_host is null)
            return Unavailable();
        var task = FindTask(settings, taskId);
        if (task is null)
            return new(false, "未找到桌面任务。");
        if (!task.Enabled)
            return new(false, "该桌面任务已停用。");

        var result = await RunTaskCoreAsync(
            task,
            Guid.NewGuid(),
            cancellationToken).ConfigureAwait(false);
        return result is null
            ? new(false, "该环境已有任务正在运行。")
            : ToStatus(result);
    }

    public async Task<DesktopSessionActionStatus> CancelLatestRunAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Guid runId;
        lock (_sync)
        {
            if (_activeRuns.Count == 0)
                return new(false, "当前没有可取消的桌面任务。");
            runId = _activeRuns.Values.Last();
        }

        if (_host is null)
            return Unavailable();
        try
        {
            var canceled = await _host.CancelRunAsync(
                runId,
                cancellationToken).ConfigureAwait(false);
            AddActivity(canceled
                ? $"已请求取消任务运行：{runId:D}"
                : $"任务运行已结束：{runId:D}");
            return new(
                canceled,
                canceled
                    ? "已提交取消请求。"
                    : "该任务运行已经结束。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AddActivity(
                $"取消任务失败：{runId:D}（{exception.GetType().Name}）");
            return new(false, $"取消任务失败：{exception.Message}");
        }
    }

    public async Task<int> ProcessScheduledAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = _getSettings();
        if (!settings.DesktopSessions.Enabled || _host is null)
            return 0;

        var now = _timeProvider.GetUtcNow();
        DateTimeOffset from;
        lock (_sync)
        {
            from = _lastScheduleCheck ?? now;
            _lastScheduleCheck = now;
        }

        if (now <= from)
            return 0;
        return await _scheduler.ProcessScheduledAsync(
            settings.DesktopSessions.Tasks,
            from,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ProcessEnvironmentReadyAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = _getSettings();
        if (!settings.DesktopSessions.Enabled || _host is null)
            return false;
        var started = false;
        foreach (var task in settings.DesktopSessions.Tasks.Where(
                     task =>
                         task.EnvironmentId == environmentId &&
                         task.Trigger.Kind ==
                         TaskTriggerKind.EnvironmentReady))
        {
            started |= await _scheduler.ProcessEnvironmentReadyAsync(
                task,
                cancellationToken).ConfigureAwait(false);
        }

        return started;
    }

    public IReadOnlyList<string> GetRecentActivity()
    {
        lock (_activitySync)
        {
            return _activity.Count == 0
                ? ["尚无桌面会话活动。"]
                : _activity.Reverse().ToArray();
        }
    }

    public bool IsEnvironmentReady(Guid environmentId) =>
        _host?.IsEnvironmentReady(environmentId) == true;

    public Guid? GetLoginInstanceId(Guid environmentId) =>
        _host?.GetLoginInstanceId(environmentId);

    public async Task<DesktopTaskRunResult> RunAsync(
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_host is null)
        {
            return new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Failed,
                CompletedSteps: 0,
                FailedStepId: null,
                ErrorCode: "DesktopSessionRuntimeUnavailable");
        }

        return await RunTaskCoreAsync(
            task,
            runId,
            cancellationToken).ConfigureAwait(false) ??
            new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Failed,
                CompletedSteps: 0,
                FailedStepId: null,
                ErrorCode: "TaskAlreadyRunning");
    }

    public async Task<DesktopSessionControlResponse> HandleAsync(
        DesktopSessionControlRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var settings = _getSettings();
        if (request.Operation ==
            DesktopSessionControlProtocol.ListOperation)
        {
            return new DesktopSessionControlResponse(
                Success: true,
                ErrorCode: null,
                Message: null,
                Environments: settings.DesktopSessions.Environments
                    .Select(environment =>
                        new DesktopSessionControlEnvironment(
                            environment.Id,
                            environment.Name,
                            environment.Backend.ToString()))
                    .ToArray(),
                Tasks: settings.DesktopSessions.Tasks
                    .Select(task => new DesktopSessionControlTask(
                        task.Id,
                        task.Name,
                        task.EnvironmentId,
                        task.Enabled))
                    .ToArray(),
                RunId: null,
                RunOutcome: null,
                CompletedSteps: null,
                FailedStepId: null);
        }

        if (request.Operation ==
            DesktopSessionControlProtocol.RunOperation)
        {
            if (!settings.DesktopSessions.Enabled)
            {
                return DesktopSessionControlResponse.Fail(
                    "FeatureDisabled",
                    "桌面分身功能已关闭。");
            }

            if (request.TaskId is not { } taskId ||
                request.EnvironmentId is not { } environmentId)
            {
                return DesktopSessionControlResponse.Fail(
                    "InvalidRequest",
                    "必须指定环境和任务。");
            }

            var task = FindTask(settings, taskId);
            if (task is null || task.EnvironmentId != environmentId)
            {
                return DesktopSessionControlResponse.Fail(
                    "TaskNotFound",
                    "未找到指定的桌面任务。");
            }

            if (!task.Enabled)
            {
                return DesktopSessionControlResponse.Fail(
                    "TaskDisabled",
                    "该桌面任务已停用。");
            }

            if (_host is null)
                return FromStatus(Unavailable());

            var result = await RunTaskCoreAsync(
                task,
                Guid.NewGuid(),
                cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return DesktopSessionControlResponse.Fail(
                    "TaskAlreadyRunning",
                    "该环境已有任务正在运行。");
            }

            var succeeded =
                result.Outcome == DesktopTaskRunOutcome.Succeeded;
            return new DesktopSessionControlResponse(
                succeeded,
                succeeded ? null : result.ErrorCode ?? "RunFailed",
                ToStatus(result).Message,
                Environments: null,
                Tasks: null,
                RunId: result.RunId,
                RunOutcome: result.Outcome.ToString(),
                CompletedSteps: result.CompletedSteps,
                FailedStepId: result.FailedStepId);
        }

        if (request.Operation ==
            DesktopSessionControlProtocol.CancelOperation)
        {
            if (request.RunId is not { } runId)
            {
                return DesktopSessionControlResponse.Fail(
                    "InvalidRequest",
                    "必须指定运行 ID。");
            }

            if (_host is null)
                return FromStatus(Unavailable());
            var canceled = await _host.CancelRunAsync(
                runId,
                cancellationToken).ConfigureAwait(false);
            return new DesktopSessionControlResponse(
                canceled,
                canceled ? null : "RunNotFound",
                canceled ? "已提交取消请求。" : "未找到该任务运行。",
                Environments: null,
                Tasks: null,
                RunId: runId,
                RunOutcome: canceled ? "CancelRequested" : null,
                CompletedSteps: null,
                FailedStepId: null);
        }

        return DesktopSessionControlResponse.Fail(
            "UnsupportedOperation",
            "不支持的桌面会话操作。");
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _activeRuns.Clear();
        }
    }

    private async Task<DesktopTaskRunResult?> RunTaskCoreAsync(
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (_host is null)
            return null;
        lock (_sync)
        {
            if (_activeRuns.ContainsKey(task.EnvironmentId))
                return null;
            _activeRuns[task.EnvironmentId] = runId;
        }

        AddActivity($"任务开始：{task.Name}");
        try
        {
            var result = await _host.RunTaskAsync(
                task,
                runId,
                cancellationToken).ConfigureAwait(false);
            AddActivity(
                $"任务结束：{task.Name}（{result.Outcome}，步骤 {result.CompletedSteps}）");
            return result;
        }
        catch (OperationCanceledException)
        {
            AddActivity($"任务已取消：{task.Name}");
            throw;
        }
        catch (Exception exception)
        {
            AddActivity(
                $"任务异常：{task.Name}（{exception.GetType().Name}）");
            var result = new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Failed,
                CompletedSteps: 0,
                FailedStepId: null,
                ErrorCode: exception.GetType().Name);
            return result;
        }
        finally
        {
            lock (_sync)
                _activeRuns.Remove(task.EnvironmentId);
        }
    }

    private static DesktopSessionActionStatus ToStatus(
        DesktopTaskRunResult result) =>
        result.Outcome switch
        {
            DesktopTaskRunOutcome.Succeeded =>
                new(true, $"任务已完成（{result.CompletedSteps} 个步骤）。"),
            DesktopTaskRunOutcome.Cancelled =>
                new(false, "任务已取消。"),
            DesktopTaskRunOutcome.UnknownOutcome =>
                new(false, "任务结果未知，请检查目标会话，未自动重试。"),
            _ =>
                new(false, $"任务失败：{result.ErrorCode ?? "Unknown"}")
        };

    private static DesktopSessionControlResponse FromStatus(
        DesktopSessionActionStatus status) =>
        DesktopSessionControlResponse.Fail(
            status.Success ? "RequestRejected" : "Unavailable",
            status.Message);

    private static EnvironmentProfile? FindEnvironment(
        AppSettings settings,
        Guid environmentId) =>
        settings.DesktopSessions.Environments.FirstOrDefault(
            environment => environment.Id == environmentId);

    private static DesktopSessionTask? FindTask(
        AppSettings settings,
        Guid taskId) =>
        settings.DesktopSessions.Tasks.FirstOrDefault(
            task => task.Id == taskId);

    private void AddActivity(string message)
    {
        lock (_activitySync)
        {
            while (_activity.Count >= MaximumActivityEntries)
                _activity.Dequeue();
            _activity.Enqueue(message);
        }
    }

    private static DesktopSessionActionStatus Disabled() =>
        new(false, "桌面分身功能已关闭。");

    private static DesktopSessionActionStatus Unavailable() =>
        new(
            false,
            "桌面会话运行时未准备；不会回退到主桌面执行。");

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
