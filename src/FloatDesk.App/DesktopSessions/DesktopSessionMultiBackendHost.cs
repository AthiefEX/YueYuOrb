using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed class DesktopSessionProductionHost :
    IDesktopSessionExecutionHost, IDesktopSessionExecutionShutdown, IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Dictionary<DesktopSessionBackendKind, SingleBackendDesktopSessionHost> _hosts;
    private readonly Dictionary<Guid, SingleBackendDesktopSessionHost> _environments = [];
    private readonly object _sync = new();
    private bool _disposed;

    public DesktopSessionProductionHost(
        Func<AppSettings> getSettings,
        Func<EnvironmentProfile, DesktopSessionBackendBundle> backendFactory,
        IStartupExecutionLedger startupLedger,
        IClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(getSettings);
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(startupLedger);
        _getSettings = getSettings;
        _hosts = Enum.GetValues<DesktopSessionBackendKind>().ToDictionary(
            kind => kind,
            _ => new SingleBackendDesktopSessionHost(
                getSettings, backendFactory, startupLedger, clock, timeProvider));
    }

    public bool IsEnvironmentReady(Guid environmentId) => Find(environmentId)?.IsEnvironmentReady(environmentId) == true;

    public Guid? GetLoginInstanceId(Guid environmentId) => Find(environmentId)?.GetLoginInstanceId(environmentId);

    public SessionRuntimeStatus GetStatus(Guid environmentId) => Find(environmentId)?.GetStatus(environmentId) ?? SessionRuntimeStatus.Stopped;

    public async Task<DesktopSessionActionStatus> StartEnvironmentAsync(
        EnvironmentProfile environment, CancellationToken cancellationToken)
    {
        SingleBackendDesktopSessionHost host;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_hosts.TryGetValue(environment.Backend, out host!))
                return new(false, "不支持的桌面分身模式。");
            if (_environments.TryGetValue(environment.Id, out var existing) && existing != host)
                return new(false, "请先停止现有环境，再更改其模式。");
            _environments[environment.Id] = host;
        }
        try
        {
            var result = await host.StartEnvironmentAsync(environment, cancellationToken).ConfigureAwait(false);
            if (!result.Success && host.GetStatus(environment.Id) == SessionRuntimeStatus.Stopped)
            {
                lock (_sync)
                    _environments.Remove(environment.Id);
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, $"桌面环境启动失败：{exception.Message}");
        }
    }

    public async Task<DesktopSessionActionStatus> StopEnvironmentAsync(Guid environmentId, CancellationToken cancellationToken)
    {
        var host = Find(environmentId);
        if (host is null)
            return new(false, "该桌面环境当前未运行。");
        var result = await host.StopEnvironmentAsync(environmentId, cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            lock (_sync)
                _environments.Remove(environmentId);
        }
        return result;
    }

    public async Task<DesktopTaskRunResult> RunTaskAsync(DesktopSessionTask task, Guid runId, CancellationToken cancellationToken)
    {
        var environment = _getSettings().DesktopSessions.Environments.FirstOrDefault(item => item.Id == task.EnvironmentId)
            ?? throw new InvalidOperationException("The task environment no longer exists.");
        var start = await StartEnvironmentAsync(environment, cancellationToken).ConfigureAwait(false);
        if (!start.Success)
            return new(runId, task.Id, DesktopTaskRunOutcome.Failed, 0, null, "EnvironmentStartFailed");
        return await Find(environment.Id)!.RunTaskAsync(task, runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CancelRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        foreach (var host in _hosts.Values)
            if (await host.CancelRunAsync(runId, cancellationToken).ConfigureAwait(false))
                return true;
        return false;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_hosts.Values.Select(host => host.ShutdownAsync(cancellationToken)));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        foreach (var host in _hosts.Values)
            host.Dispose();
    }

    private SingleBackendDesktopSessionHost? Find(Guid environmentId)
    {
        lock (_sync)
            return _environments.GetValueOrDefault(environmentId);
    }
}
