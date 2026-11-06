using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

public interface IChildSessionLauncher
{
    Task<ChildSessionLaunch> LaunchAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken);

    Task ShowAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task HideAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);

    Task DisconnectAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);
}

public interface IChildSessionConnection : IAsyncDisposable
{
}

public sealed record ChildSessionLaunch(
    uint SessionId,
    Guid Generation,
    Guid LoginInstanceId,
    IChildSessionConnection Connection);

public sealed class ChildSessionBackend :
    IDesktopSessionBackend,
    IAsyncDisposable
{
    private const uint NoChildSession = uint.MaxValue;

    private readonly IChildSessionNative _native;
    private readonly IChildSessionLauncher _launcher;
    private readonly Func<Guid> _loginInstanceFactory;
    private readonly Func<uint> _parentSessionIdProvider;
    private readonly object _sync = new();
    private ActiveSession? _active;
    private bool _launching;
    private bool _disposed;

    public ChildSessionBackend(
        IChildSessionNative native,
        IChildSessionLauncher launcher,
        Func<Guid>? loginInstanceFactory = null,
        Func<uint>? parentSessionIdProvider = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _launcher = launcher ??
            throw new ArgumentNullException(nameof(launcher));
        _loginInstanceFactory =
            loginInstanceFactory ?? (() => Guid.NewGuid());
        _parentSessionIdProvider =
            parentSessionIdProvider ?? GetCurrentProcessSessionId;
    }

    public Task<SessionCapabilities> ProbeAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        return Task.FromResult(CreateCapabilities());
    }

    public Task<PreparationResult> PrepareAsync(
        EnvironmentProfile profile,
        SessionStartupPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var capabilities = CreateCapabilities();
        if (profile.Backend != DesktopSessionBackendKind.LocalChild)
        {
            return Task.FromResult(Failed(
                capabilities,
                "UnsupportedBackend"));
        }

        if (!_native.SupportsChildSessions)
        {
            return Task.FromResult(Failed(
                capabilities,
                "ChildSessionsUnavailable"));
        }

        if (!_native.TryGetEnabled(out var enabled))
        {
            return Task.FromResult(Failed(
                capabilities,
                "ChildSessionStateUnavailable"));
        }

        if (!enabled)
        {
            return Task.FromResult(Failed(
                capabilities,
                "ChildSessionsDisabled"));
        }

        if (policy == SessionStartupPolicy.ManagedOnly &&
            !capabilities.CanSuppressStartupBeforeLaunch)
        {
            return Task.FromResult(Failed(
                capabilities,
                "ManagedOnlyUnsupported"));
        }

        return Task.FromResult(new PreparationResult(
            true,
            RequiresNewLogin: true,
            capabilities,
            ErrorCode: null));
    }

    public async Task<SessionHandle> StartAsync(
        EnvironmentProfile profile,
        PreparationResult preparation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preparation);
        ThrowIfDisposed();
        if (!preparation.Succeeded)
        {
            throw new InvalidOperationException(
                preparation.ErrorCode ??
                "Desktop session preparation failed.");
        }

        if (profile.Backend != DesktopSessionBackendKind.LocalChild)
        {
            throw new InvalidOperationException(
                "This backend only supports local child sessions.");
        }

        lock (_sync)
        {
            if (_launching || _active is not null)
            {
                throw new InvalidOperationException(
                    "A local child session is already active.");
            }

            if (TryGetExistingChildSessionId(out var existingSessionId))
            {
                throw new InvalidOperationException(
                    $"Child session {existingSessionId} already exists and is not owned by this application.");
            }

            _launching = true;
        }

        ChildSessionLaunch? launch = null;
        uint? confirmedSessionId = null;
        try
        {
            launch = await _launcher.LaunchAsync(
                profile,
                cancellationToken).ConfigureAwait(false);

            var parentSessionId = _parentSessionIdProvider();
            if (launch.SessionId == 0 ||
                launch.SessionId == NoChildSession ||
                launch.SessionId == parentSessionId)
            {
                throw new InvalidOperationException(
                    "The launcher returned an invalid child session identity.");
            }

            confirmedSessionId = launch.SessionId;
            cancellationToken.ThrowIfCancellationRequested();

            var generation = launch.Generation == Guid.Empty
                ? Guid.NewGuid()
                : launch.Generation;
            var loginInstanceId = launch.LoginInstanceId == Guid.Empty
                ? _loginInstanceFactory()
                : launch.LoginInstanceId;
            var handle = new SessionHandle(
                profile.Id,
                generation,
                launch.SessionId,
                loginInstanceId,
                preparation.Succeeded);
            var active = new ActiveSession(handle, launch.Connection);

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_active is not null)
                {
                    throw new InvalidOperationException(
                        "A local child session is already active.");
                }

                _active = active;
            }

            launch = null;
            return handle;
        }
        catch
        {
            if (launch is not null)
            {
                await DisposeConnectionAsync(launch.Connection)
                    .ConfigureAwait(false);
                TryCleanupLaunchedSession(confirmedSessionId);
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _launching = false;
            }
        }
    }

    public async Task ShowAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureActive(handle);
        await _launcher.ShowAsync(
            handle,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(
        SessionHandle handle,
        SessionStopMode mode,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var active = GetActive(handle);
        switch (mode)
        {
            case SessionStopMode.HideView:
                await _launcher.HideAsync(
                    handle,
                    cancellationToken).ConfigureAwait(false);
                return;

            case SessionStopMode.Disconnect:
                await DisconnectOwnedAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);
                return;

            case SessionStopMode.LogoffOwnedSession:
                await DisconnectOwnedAsync(
                    active,
                    cancellationToken).ConfigureAwait(false);
                EnsureChildSessionCanBeLoggedOff(active.Handle);
                if (!_native.TryLogoffSession(
                        active.Handle.WindowsSessionId))
                {
                    throw new InvalidOperationException(
                        "The owned child session could not be logged off.");
                }

                return;

            case SessionStopMode.ShutdownGuest:
                throw new NotSupportedException(
                    "ShutdownGuest is not supported by the local child-session backend.");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mode),
                    mode,
                    "Unknown session stop mode.");
        }
    }

    public async IAsyncEnumerable<SessionBackendEvent> WatchAsync(
        Guid environmentId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ActiveSession? active;
        lock (_sync)
        {
            active = _active;
        }

        if (active is null ||
            active.Handle.EnvironmentId != environmentId)
        {
            yield break;
        }

        await foreach (var backendEvent in active.Events.Reader.ReadAllAsync(
                           cancellationToken).ConfigureAwait(false))
        {
            yield return backendEvent;
        }
    }

    public async ValueTask DisposeAsync()
    {
        ActiveSession? active;
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
            active.Events.Writer.TryComplete();
            await DisposeConnectionAsync(active.Connection)
                .ConfigureAwait(false);
        }
    }

    private async Task DisconnectOwnedAsync(
        ActiveSession active,
        CancellationToken cancellationToken)
    {
        await _launcher.DisconnectAsync(
            active.Handle,
            cancellationToken).ConfigureAwait(false);
        await DisposeConnectionAsync(active.Connection)
            .ConfigureAwait(false);

        lock (_sync)
        {
            if (ReferenceEquals(_active, active))
                _active = null;
        }

        active.Events.Writer.TryWrite(new SessionBackendEvent(
            active.Handle.EnvironmentId,
            active.Handle.Generation,
            DateTimeOffset.UtcNow,
            SessionBackendEventKind.Disconnected,
            active.Handle.LoginInstanceId,
            Width: null,
            Height: null));
        active.Events.Writer.TryComplete();
    }

    private void EnsureChildSessionCanBeLoggedOff(SessionHandle handle)
    {
        if (handle.WindowsSessionId == _parentSessionIdProvider())
        {
            throw new InvalidOperationException(
                "The parent session must never be logged off.");
        }

        if (!_native.TryGetChildSessionId(out var currentSessionId) ||
            currentSessionId != handle.WindowsSessionId)
        {
            throw new InvalidOperationException(
                "The child session identity can no longer be verified.");
        }
    }

    private bool TryGetExistingChildSessionId(out uint sessionId)
    {
        if (!_native.TryGetChildSessionId(out sessionId))
            return false;
        return sessionId is not (0 or NoChildSession);
    }

    private void TryCleanupLaunchedSession(uint? sessionId)
    {
        if (sessionId is null ||
            sessionId == _parentSessionIdProvider() ||
            !_native.TryGetChildSessionId(out var currentSessionId) ||
            currentSessionId != sessionId)
        {
            return;
        }

        _ = _native.TryLogoffSession(currentSessionId);
    }

    private ActiveSession GetActive(SessionHandle handle)
    {
        lock (_sync)
        {
            var active = _active;
            if (active is null || active.Handle != handle)
            {
                throw new InvalidOperationException(
                    "The child session handle is stale or not owned by this backend.");
            }

            return active;
        }
    }

    private void EnsureActive(SessionHandle handle) => _ = GetActive(handle);

    private static SessionCapabilities CreateCapabilities() => new(
        CanRunHidden: false,
        CanSuppressStartupBeforeLaunch: false,
        SupportedStartupSources: ["EnvironmentDefault"],
        UnsupportedStartupSources:
        [
            "Hidden view mode has not been validated.",
            "ManagedOnly startup suppression is unavailable."
        ]);

    private static PreparationResult Failed(
        SessionCapabilities capabilities,
        string errorCode) =>
        new(false, false, capabilities, errorCode);

    private static async ValueTask DisposeConnectionAsync(
        IChildSessionConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static uint GetCurrentProcessSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return unchecked((uint)process.SessionId);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ActiveSession(
        SessionHandle handle,
        IChildSessionConnection connection)
    {
        public SessionHandle Handle { get; } = handle;
        public IChildSessionConnection Connection { get; } = connection;
        public Channel<SessionBackendEvent> Events { get; } =
            Channel.CreateUnbounded<SessionBackendEvent>();
    }
}
