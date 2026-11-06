using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed class DesktopSessionRuntime : IDisposable
{
    private static readonly TimeSpan DefaultInputLeaseDuration =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultInputLeaseRenewInterval =
        TimeSpan.FromSeconds(1);

    private readonly IDesktopSessionBackend _backend;
    private readonly IDesktopSessionTransport _transport;
    private readonly IReadOnlyDictionary<Guid, EnvironmentState> _states;
    private readonly TimeSpan _inputLeaseDuration;
    private readonly TimeSpan _inputLeaseRenewInterval;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private bool _disposed;

    public DesktopSessionRuntime(
        IDesktopSessionBackend backend,
        IDesktopSessionTransport transport,
        IEnumerable<EnvironmentProfile> profiles,
        TimeSpan? inputLeaseDuration = null,
        TimeSpan? inputLeaseRenewInterval = null,
        TimeProvider? timeProvider = null)
    {
        _backend = backend ??
            throw new ArgumentNullException(nameof(backend));
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));
        ArgumentNullException.ThrowIfNull(profiles);
        _inputLeaseDuration =
            inputLeaseDuration ?? DefaultInputLeaseDuration;
        _inputLeaseRenewInterval =
            inputLeaseRenewInterval ?? DefaultInputLeaseRenewInterval;
        if (_inputLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputLeaseDuration),
                "The input lease duration must be positive.");
        }

        if (_inputLeaseRenewInterval <= TimeSpan.Zero ||
            _inputLeaseRenewInterval >= _inputLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputLeaseRenewInterval),
                "The input lease renewal interval must be positive and shorter than the lease.");
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        var states = new Dictionary<Guid, EnvironmentState>();
        foreach (var profile in profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (!states.TryAdd(
                    profile.Id,
                    new EnvironmentState(profile)))
            {
                throw new ArgumentException(
                    "Desktop session environment IDs must be unique.",
                    nameof(profiles));
            }
        }

        _states = states;
    }

    public async Task<SessionHandle> StartAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var state = GetState(environmentId);
        lock (_sync)
        {
            if (state.Handle is not null ||
                state.Status is not (
                SessionRuntimeStatus.Stopped or
                SessionRuntimeStatus.Faulted))
            {
                throw new InvalidOperationException(
                    "The desktop session is not stopped.");
            }

            state.Status = SessionRuntimeStatus.Preparing;
            state.ManualControl = false;
        }

        SessionHandle? startedHandle = null;
        try
        {
            var preparation = await _backend.PrepareAsync(
                state.Profile,
                state.Profile.StartupPolicy,
                cancellationToken).ConfigureAwait(false);
            if (!preparation.Succeeded)
            {
                throw new InvalidOperationException(
                    preparation.ErrorCode ??
                    "Desktop session preparation failed.");
            }

            lock (_sync)
            {
                state.Status = SessionRuntimeStatus.Starting;
            }

            startedHandle = await DesktopSessionStartCoordinator.StartAsync(
                _backend,
                state.Profile,
                preparation,
                cancellationToken).ConfigureAwait(false);
            if (startedHandle.EnvironmentId != environmentId)
            {
                throw new InvalidOperationException(
                    "The backend returned a handle for a different environment.");
            }

            var handshake = await _transport.ConnectAsync(
                startedHandle,
                cancellationToken).ConfigureAwait(false);
            if (!handshake.Succeeded ||
                handshake.Generation != startedHandle.Generation ||
                handshake.LoginInstanceId != startedHandle.LoginInstanceId)
            {
                throw new InvalidOperationException(
                    handshake.ErrorCode ??
                    "Desktop session transport authentication failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            CancellationTokenSource watcherCancellation;
            lock (_sync)
            {
                ThrowIfDisposed();
                state.Handle = startedHandle;
                state.Status = SessionRuntimeStatus.Ready;
                state.ManualControl = false;
                watcherCancellation = new CancellationTokenSource();
                state.WatcherCancellation = watcherCancellation;
            }

            state.WatcherTask = WatchBackendAsync(
                state,
                startedHandle,
                watcherCancellation.Token);
            return startedHandle;
        }
        catch (OperationCanceledException)
        {
            var cleaned = await CleanupStartedSessionAsync(
                state,
                startedHandle)
                .ConfigureAwait(false);
            lock (_sync)
            {
                state.Handle = cleaned
                    ? null
                    : startedHandle;
                state.Status = cleaned
                    ? SessionRuntimeStatus.Stopped
                    : SessionRuntimeStatus.Faulted;
            }

            throw;
        }
        catch
        {
            var cleaned = await CleanupStartedSessionAsync(
                state,
                startedHandle).ConfigureAwait(false);
            lock (_sync)
            {
                state.Handle = cleaned
                    ? null
                    : startedHandle;
                state.Status = cleaned
                    ? SessionRuntimeStatus.Stopped
                    : SessionRuntimeStatus.Faulted;
            }

            throw;
        }
    }

    public async Task<CommandResult> ExecuteAsync(
        SessionHandle handle,
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Handle != handle)
        {
            throw new InvalidOperationException(
                "The command must be bound to the supplied session handle.");
        }

        var state = GetState(handle.EnvironmentId);
        lock (_sync)
        {
            EnsureCurrentHandleLocked(state, handle);
            if (state.ManualControl ||
                state.Status is not (
                    SessionRuntimeStatus.Ready or
                    SessionRuntimeStatus.Running))
            {
                return Rejected(command.CommandId, "SessionNotReady");
            }

            // One command may run while MaxQueue commands wait behind it.
            if (state.PendingCommands >=
                state.Profile.Limits.MaxQueue + 1)
            {
                throw new InvalidOperationException(
                    "The desktop session command queue is full.");
            }

            state.PendingCommands++;
        }

        var gateHeld = false;
        try
        {
            await state.DispatchGate.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            gateHeld = true;

            ActiveCommand dispatch;
            lock (_sync)
            {
                EnsureCurrentHandleLocked(state, handle);
                if (state.ManualControl ||
                    state.Status is not (
                        SessionRuntimeStatus.Ready or
                        SessionRuntimeStatus.Running))
                {
                    return Rejected(command.CommandId, "SessionNotReady");
                }

                if (command.DeadlineUtc <= _timeProvider.GetUtcNow())
                    return Rejected(command.CommandId, "CommandExpired");

                dispatch = new ActiveCommand(command, cancellationToken);
                state.ActiveCommand = dispatch;
                state.Status = SessionRuntimeStatus.Running;
            }

            var leaseAcquired = false;
            var sent = false;
            try
            {
                await _transport.AcquireInputLeaseAsync(
                    handle,
                    _inputLeaseDuration,
                    dispatch.Cancellation.Token).ConfigureAwait(false);
                leaseAcquired = true;
                dispatch.RenewalTask = RenewInputLeaseAsync(
                    handle,
                    dispatch);

                lock (_sync)
                {
                    if (dispatch.OutcomeUnknown ||
                        state.ManualControl)
                    {
                        return Unknown(command.CommandId);
                    }
                }

                sent = true;
                var result = await _transport.SendAsync(
                    handle,
                    command,
                    dispatch.Cancellation.Token).ConfigureAwait(false);
                if (dispatch.OutcomeUnknown)
                    return Unknown(command.CommandId);
                return result;
            }
            catch (OperationCanceledException)
                when (dispatch.OutcomeUnknown)
            {
                return Unknown(command.CommandId);
            }
            catch (OperationCanceledException) when (sent)
            {
                return Unknown(command.CommandId);
            }
            finally
            {
                dispatch.Cancel();
                if (dispatch.RenewalTask is not null)
                {
                    try
                    {
                        await dispatch.RenewalTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                dispatch.Cancellation.Dispose();
                if (leaseAcquired)
                {
                    try
                    {
                        await _transport.ReleaseInputLeaseAsync(
                            handle,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        MarkFaulted(state, handle);
                    }
                }

                lock (_sync)
                {
                    if (ReferenceEquals(state.ActiveCommand, dispatch))
                        state.ActiveCommand = null;
                    if (state.Status == SessionRuntimeStatus.Running &&
                        !state.ManualControl &&
                        state.Handle == handle)
                    {
                        state.Status = SessionRuntimeStatus.Ready;
                    }
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                state.PendingCommands--;
            }

            if (gateHeld)
                state.DispatchGate.Release();
        }
    }

    public async Task TakeManualControlAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var state = GetState(environmentId);
        SessionHandle handle;
        ActiveCommand? active;
        lock (_sync)
        {
            handle = state.Handle ??
                throw new InvalidOperationException(
                    "The desktop session is not active.");
            if (state.Status is not (
                SessionRuntimeStatus.Ready or
                SessionRuntimeStatus.Running))
            {
                throw new InvalidOperationException(
                    "Manual control is unavailable in the current state.");
            }

            state.ManualControl = true;
            state.Status = SessionRuntimeStatus.Paused;
            active = state.ActiveCommand;
            if (active is not null)
            {
                active.MarkOutcomeUnknown();
                active.Cancel();
            }
        }

        try
        {
            if (active is not null)
            {
                await _transport.CancelAsync(
                    handle,
                    active.Command.CommandId,
                    cancellationToken).ConfigureAwait(false);
            }

            await _transport.ReleaseInputLeaseAsync(
                handle,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            MarkFaulted(state, handle);
            throw;
        }
    }

    public Task ResumeAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState(environmentId);
        lock (_sync)
        {
            if (!state.ManualControl ||
                state.Status != SessionRuntimeStatus.Paused ||
                state.Handle is null)
            {
                throw new InvalidOperationException(
                    "The desktop session is not paused for manual control.");
            }

            state.ManualControl = false;
            state.Status = SessionRuntimeStatus.Ready;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(
        Guid environmentId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var state = GetState(environmentId);
        SessionHandle? handle;
        CancellationTokenSource? watcherCancellation;
        lock (_sync)
        {
            if (state.Status == SessionRuntimeStatus.Stopped &&
                state.Handle is null)
            {
                return;
            }

            handle = state.Handle;
            state.Status = SessionRuntimeStatus.Stopping;
            state.ManualControl = false;
            state.ActiveCommand?.MarkOutcomeUnknown();
            state.ActiveCommand?.Cancel();
            watcherCancellation = state.WatcherCancellation;
            state.WatcherCancellation = null;
        }

        watcherCancellation?.Cancel();
        Exception? cleanupError = null;
        if (handle is not null)
        {
            try
            {
                await _transport.ReleaseInputLeaseAsync(
                    handle,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                await _backend.StopAsync(
                    handle,
                    SessionStopMode.Disconnect,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupError ??= exception;
            }
        }

        lock (_sync)
        {
            if (cleanupError is null &&
                state.Handle == handle)
            {
                state.Handle = null;
            }
            state.Status = cleanupError is null
                ? SessionRuntimeStatus.Stopped
                : SessionRuntimeStatus.Faulted;
        }

        watcherCancellation?.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        if (cleanupError is not null)
            throw new InvalidOperationException(
                "Desktop session cleanup failed.",
                cleanupError);
    }

    public SessionRuntimeStatus GetStatus(Guid environmentId)
    {
        var state = GetState(environmentId);
        lock (_sync)
        {
            return state.Status;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource[] watchers;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            watchers = _states.Values
                .Select(state => state.WatcherCancellation)
                .Where(source => source is not null)
                .Cast<CancellationTokenSource>()
                .ToArray();
            foreach (var source in watchers)
                source.Cancel();
            foreach (var state in _states.Values)
            {
                state.ActiveCommand?.MarkOutcomeUnknown();
                state.ActiveCommand?.Cancel();
            }
        }

        foreach (var source in watchers)
            source.Dispose();
    }

    private async Task<bool> CleanupStartedSessionAsync(
        EnvironmentState state,
        SessionHandle? handle)
    {
        if (handle is null)
            return true;
        try
        {
            await _transport.ReleaseInputLeaseAsync(
                handle,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await _backend.StopAsync(
                handle,
                SessionStopMode.Disconnect,
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task WatchBackendAsync(
        EnvironmentState state,
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var backendEvent in _backend.WatchAsync(
                               state.Profile.Id,
                               cancellationToken).ConfigureAwait(false))
            {
                ApplyBackendEvent(state, handle, backendEvent);
            }

            MarkReconnectingIfCurrent(state, handle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            MarkReconnectingIfCurrent(state, handle);
        }
    }

    private void ApplyBackendEvent(
        EnvironmentState state,
        SessionHandle handle,
        SessionBackendEvent backendEvent)
    {
        if (backendEvent.EnvironmentId != handle.EnvironmentId ||
            backendEvent.Generation != handle.Generation ||
            backendEvent.LoginInstanceId != handle.LoginInstanceId)
        {
            return;
        }

        lock (_sync)
        {
            if (state.Handle != handle)
                return;
            switch (backendEvent.Kind)
            {
                case SessionBackendEventKind.Disconnected:
                case SessionBackendEventKind.Locked:
                case SessionBackendEventKind.AgentExited:
                    state.Status = SessionRuntimeStatus.Reconnecting;
                    state.ActiveCommand?.MarkOutcomeUnknown();
                    state.ActiveCommand?.Cancel();
                    break;
                case SessionBackendEventKind.SessionEnded:
                    state.Status = SessionRuntimeStatus.Stopped;
                    state.Handle = null;
                    state.ManualControl = false;
                    state.ActiveCommand?.MarkOutcomeUnknown();
                    state.ActiveCommand?.Cancel();
                    break;
            }
        }
    }

    private void MarkReconnectingIfCurrent(
        EnvironmentState state,
        SessionHandle handle)
    {
        lock (_sync)
        {
            if (state.Handle == handle &&
                state.Status is not (
                    SessionRuntimeStatus.Stopping or
                    SessionRuntimeStatus.Stopped or
                    SessionRuntimeStatus.Faulted))
            {
                state.Status = SessionRuntimeStatus.Reconnecting;
                state.ActiveCommand?.MarkOutcomeUnknown();
                state.ActiveCommand?.Cancel();
            }
        }
    }

    private async Task RenewInputLeaseAsync(
        SessionHandle handle,
        ActiveCommand dispatch)
    {
        try
        {
            using var timer = new PeriodicTimer(
                _inputLeaseRenewInterval,
                _timeProvider);
            while (await timer.WaitForNextTickAsync(
                       dispatch.Cancellation.Token).ConfigureAwait(false))
            {
                await _transport.RenewInputLeaseAsync(
                    handle,
                    dispatch.Cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (dispatch.Cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            dispatch.MarkOutcomeUnknown();
            dispatch.Cancel();
        }
    }

    private void MarkFaulted(
        EnvironmentState state,
        SessionHandle handle)
    {
        lock (_sync)
        {
            if (state.Handle == handle)
            {
                state.Status = SessionRuntimeStatus.Faulted;
                state.ManualControl = false;
                state.ActiveCommand?.MarkOutcomeUnknown();
                state.ActiveCommand?.Cancel();
            }
        }
    }

    private static void EnsureCurrentHandleLocked(
        EnvironmentState state,
        SessionHandle handle)
    {
        if (state.Handle is null ||
            state.Handle.EnvironmentId != handle.EnvironmentId ||
            state.Handle.Generation != handle.Generation)
        {
            throw new InvalidOperationException(
                "The desktop session generation is stale.");
        }
    }

    private static CommandResult Rejected(
        Guid commandId,
        string errorCode) =>
        new(commandId, false, false, errorCode);

    private static CommandResult Unknown(Guid commandId) =>
        new(commandId, false, true, "OutcomeUnknown");

    private EnvironmentState GetState(Guid environmentId)
    {
        if (_states.TryGetValue(environmentId, out var state))
            return state;
        throw new KeyNotFoundException(
            "The desktop session environment is not configured.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class EnvironmentState(EnvironmentProfile profile)
    {
        public EnvironmentProfile Profile { get; } = profile;
        public SemaphoreSlim DispatchGate { get; } = new(1, 1);
        public SessionHandle? Handle { get; set; }
        public SessionRuntimeStatus Status { get; set; } =
            SessionRuntimeStatus.Stopped;
        public bool ManualControl { get; set; }
        public int PendingCommands { get; set; }
        public ActiveCommand? ActiveCommand { get; set; }
        public CancellationTokenSource? WatcherCancellation { get; set; }
        public Task? WatcherTask { get; set; }
    }

    private sealed class ActiveCommand(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        private int _outcomeUnknown;

        public SessionCommand Command { get; } = command;
        public CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        public Task? RenewalTask { get; set; }
        public bool OutcomeUnknown =>
            Volatile.Read(ref _outcomeUnknown) != 0;

        public void MarkOutcomeUnknown() =>
            Interlocked.Exchange(ref _outcomeUnknown, 1);

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
