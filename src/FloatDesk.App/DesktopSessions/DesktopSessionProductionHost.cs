using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed record DesktopSessionBackendBundle(
    IDesktopSessionBackend Backend,
    IDesktopSessionTransport Transport);

public interface IDesktopSessionExecutionShutdown
{
    Task ShutdownAsync(CancellationToken cancellationToken);
}

internal sealed class SingleBackendDesktopSessionHost :
    IDesktopSessionExecutionHost,
    IDesktopSessionExecutionShutdown,
    IDisposable
{
    private readonly Func<AppSettings> _getSettings;
    private readonly Func<EnvironmentProfile, DesktopSessionBackendBundle>
        _backendFactory;
    private readonly IStartupExecutionLedger _startupLedger;
    private readonly IClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private ActiveEnvironment? _active;
    private bool _disposed;

    public SingleBackendDesktopSessionHost(
        Func<AppSettings> getSettings,
        Func<EnvironmentProfile, DesktopSessionBackendBundle> backendFactory,
        IStartupExecutionLedger startupLedger,
        IClock? clock = null,
        TimeProvider? timeProvider = null)
    {
        _getSettings = getSettings ??
            throw new ArgumentNullException(nameof(getSettings));
        _backendFactory = backendFactory ??
            throw new ArgumentNullException(nameof(backendFactory));
        _startupLedger = startupLedger ??
            throw new ArgumentNullException(nameof(startupLedger));
        _clock = clock ?? new SystemClock();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsEnvironmentReady(Guid environmentId)
    {
        lock (_sync)
        {
            return _active is not null &&
                _active.Environment.Id == environmentId &&
                _active.Runtime.GetStatus(environmentId) is
                    SessionRuntimeStatus.Ready or
                    SessionRuntimeStatus.Running;
        }
    }

    public Guid? GetLoginInstanceId(Guid environmentId)
    {
        lock (_sync)
        {
            return _active is not null &&
                _active.Environment.Id == environmentId
                ? _active.Handle.LoginInstanceId
                : null;
        }
    }

    public SessionRuntimeStatus GetStatus(Guid environmentId)
    {
        lock (_sync)
        {
            return _active is not null &&
                _active.Environment.Id == environmentId
                ? _active.Runtime.GetStatus(environmentId)
                : SessionRuntimeStatus.Stopped;
        }
    }

    public async Task<DesktopSessionActionStatus> StartEnvironmentAsync(
        EnvironmentProfile environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureStartedLockedAsync(
                environment,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<DesktopSessionActionStatus> StopEnvironmentAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveEnvironment? active;
        try
        {
            lock (_sync)
                active = _active;
            if (active is null || active.Environment.Id != environmentId)
            {
                return new(false, "该桌面环境当前未运行。");
            }

            try
            {
                await active.Runtime.StopAsync(
                    environmentId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                return new(
                    false,
                    $"桌面环境停止失败：{exception.Message}");
            }

            lock (_sync)
            {
                if (ReferenceEquals(_active, active))
                    _active = null;
            }

            await DisposeActiveAsync(active).ConfigureAwait(false);
            return new(true, "桌面环境已断开。");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<DesktopTaskRunResult> RunTaskAsync(
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        ActiveEnvironment active;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _getSettings();
            var environment = settings.DesktopSessions.Environments
                .FirstOrDefault(candidate =>
                    candidate.Id == task.EnvironmentId) ??
                throw new InvalidOperationException(
                    "The task environment no longer exists.");
            var start = await EnsureStartedLockedAsync(
                environment,
                cancellationToken).ConfigureAwait(false);
            if (!start.Success)
            {
                return new DesktopTaskRunResult(
                    runId,
                    task.Id,
                    DesktopTaskRunOutcome.Failed,
                    CompletedSteps: 0,
                    FailedStepId: null,
                    ErrorCode: "EnvironmentStartFailed");
            }

            lock (_sync)
            {
                active = _active ??
                    throw new InvalidOperationException(
                        "The desktop environment is not active.");
                if (active.ActiveRunId is not null)
                {
                    return new DesktopTaskRunResult(
                        runId,
                        task.Id,
                        DesktopTaskRunOutcome.Failed,
                        CompletedSteps: 0,
                        FailedStepId: null,
                        ErrorCode: "TaskAlreadyRunning");
                }

                active.ActiveRunId = runId;
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        try
        {
            return await active.Runner.RunAsync(
                active.Handle,
                task,
                runId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_active, active) &&
                    active.ActiveRunId == runId)
                {
                    active.ActiveRunId = null;
                }
            }
        }
    }

    public async Task<bool> CancelRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ActiveEnvironment? active;
        lock (_sync)
        {
            active = _active?.ActiveRunId == runId
                ? _active
                : null;
        }

        return active is not null &&
            await active.Runner.CancelRunAsync(
                runId,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveEnvironment? active;
        try
        {
            lock (_sync)
                active = _active;
            if (active is null)
                return;
            try
            {
                await active.Runtime.StopAsync(
                    active.Environment.Id,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Process shutdown must not hide cleanup failures.
            }

            lock (_sync)
            {
                if (ReferenceEquals(_active, active))
                    _active = null;
            }

            await DisposeActiveAsync(active).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        ActiveEnvironment? active;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _active;
            _active = null;
        }

        if (active is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await active.Runtime.StopAsync(
                        active.Environment.Id,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                await DisposeActiveAsync(active).ConfigureAwait(false);
            });
        }
    }

    private async Task<DesktopSessionActionStatus> EnsureStartedLockedAsync(
        EnvironmentProfile environment,
        CancellationToken cancellationToken)
    {
        ActiveEnvironment? existing;
        lock (_sync)
            existing = _active;
        if (existing is not null)
        {
            if (existing.Environment.Id != environment.Id)
                return new(false, "每种模式仅允许一个桌面分身环境运行，请先停止该模式现有环境。");
            if (IsEnvironmentReady(environment.Id) ||
                existing.Runtime.GetStatus(environment.Id) == SessionRuntimeStatus.Paused)
            {
                await existing.Bundle.Backend.ShowAsync(existing.Handle, cancellationToken).ConfigureAwait(false);
                return new(true, "桌面环境已显示。");
            }
            if (environment.Backend != DesktopSessionBackendKind.HyperV || existing.ActiveRunId is not null)
                return new(false, "桌面环境未就绪，请先停止并重新启动。");

            // Rebuild only the stale automation connection. Hyper-V runtime Stop
            // uses Disconnect, so a live guest and its applications keep running.
            try { await existing.Runtime.StopAsync(environment.Id, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The disconnected transport can no longer acknowledge lease release.
                // A fresh authenticated generation is required before any new commands.
            }
            lock (_sync)
                if (ReferenceEquals(_active, existing)) _active = null;
            await DisposeActiveAsync(existing).ConfigureAwait(false);
        }

        var bundle = _backendFactory(environment);
        var runtime = new DesktopSessionRuntime(
            bundle.Backend,
            new TaskGuestTransport(environment, bundle.Transport),
            [environment]);
        SessionHandle? handle = null;
        try
        {
            handle = await runtime.StartAsync(
                environment.Id,
                cancellationToken).ConfigureAwait(false);
            var runtimeTransport = new RuntimeDesktopSessionTransport(
                runtime,
                bundle.Transport);
            var startup = new SessionStartupCoordinator(
                _startupLedger,
                new TransportStartupApplicationRunner(runtimeTransport),
                _clock,
                environment.StartupPolicy,
                (await bundle.Backend.ProbeAsync(
                    environment,
                    cancellationToken).ConfigureAwait(false)));
            await startup.RunOnceAsync(
                handle,
                environment.StartupEntries,
                cancellationToken).ConfigureAwait(false);
            await bundle.Backend.ShowAsync(handle, cancellationToken).ConfigureAwait(false);
            var active = new ActiveEnvironment(
                environment,
                bundle,
                runtime,
                runtimeTransport,
                new DesktopTaskRunner(
                    runtimeTransport,
                    new UnavailableDesktopTaskResolutionProvider(),
                    _clock,
                    timeProvider: _timeProvider),
                handle);
            lock (_sync)
            {
                ThrowIfDisposed();
                _active = active;
            }

            return new(true, "桌面环境已启动。");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            await CleanupPartialStartAsync(
                environment,
                bundle,
                runtime,
                handle).ConfigureAwait(false);
            return new(
                false,
                $"桌面环境启动失败：{exception.Message}");
        }
        catch
        {
            await CleanupPartialStartAsync(
                environment,
                bundle,
                runtime,
                handle).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task CleanupPartialStartAsync(
        EnvironmentProfile environment,
        DesktopSessionBackendBundle bundle,
        DesktopSessionRuntime runtime,
        SessionHandle? handle)
    {
        if (handle is not null)
        {
            try
            {
                await runtime.StopAsync(
                    environment.Id,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        runtime.Dispose();
        await DisposeAsync(bundle.Transport).ConfigureAwait(false);
        await DisposeAsync(bundle.Backend).ConfigureAwait(false);
    }

    private static async Task DisposeActiveAsync(ActiveEnvironment active)
    {
        active.Runtime.Dispose();
        await DisposeAsync(active.Bundle.Transport).ConfigureAwait(false);
        await DisposeAsync(active.Bundle.Backend).ConfigureAwait(false);
    }

    private static async ValueTask DisposeAsync(object value)
    {
        if (value is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (value is IDisposable disposable)
            disposable.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ActiveEnvironment(
        EnvironmentProfile environment,
        DesktopSessionBackendBundle bundle,
        DesktopSessionRuntime runtime,
        RuntimeDesktopSessionTransport runtimeTransport,
        DesktopTaskRunner runner,
        SessionHandle handle)
    {
        public EnvironmentProfile Environment { get; } = environment;
        public DesktopSessionBackendBundle Bundle { get; } = bundle;
        public DesktopSessionRuntime Runtime { get; } = runtime;
        public RuntimeDesktopSessionTransport RuntimeTransport { get; } =
            runtimeTransport;
        public DesktopTaskRunner Runner { get; } = runner;
        public SessionHandle Handle { get; } = handle;
        public Guid? ActiveRunId { get; set; }
    }
}

internal sealed class RuntimeDesktopSessionTransport(
    DesktopSessionRuntime runtime,
    IDesktopSessionTransport transport) : IDesktopSessionTransport
{
    public Task<SessionTransportHandshake> ConnectAsync(
        SessionHandle handle,
        CancellationToken cancellationToken) =>
        transport.ConnectAsync(handle, cancellationToken);

    public Task AcquireInputLeaseAsync(
        SessionHandle handle,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RenewInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task ReleaseInputLeaseAsync(
        SessionHandle handle,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CancelAsync(
        SessionHandle handle,
        Guid commandId,
        CancellationToken cancellationToken) =>
        transport.CancelAsync(handle, commandId, cancellationToken);

    public Task<CommandResult> SendAsync(
        SessionHandle handle,
        SessionCommand command,
        CancellationToken cancellationToken) =>
        runtime.ExecuteAsync(handle, command, cancellationToken);
}

public sealed class UnavailableDesktopTaskResolutionProvider :
    IDesktopTaskResolutionProvider
{
    public Task<DesktopSessionResolution> GetResolutionAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DesktopSessionResolution(0, 0));
    }
}
