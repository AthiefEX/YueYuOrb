using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FloatDesk.Windows.DesktopSessions;

public enum HyperVPowerState
{
    Unknown,
    Off,
    Saved,
    Running,
    Paused
}

public sealed record HyperVHostProbe(
    bool Available,
    bool VmExists,
    string? ErrorCode);

public interface IHyperVHost
{
    Task<HyperVHostProbe> ProbeAsync(
        Guid vmId,
        CancellationToken cancellationToken);

    Task<HyperVPowerState> GetPowerStateAsync(
        Guid vmId,
        CancellationToken cancellationToken);

    Task StartVmAsync(
        Guid vmId,
        CancellationToken cancellationToken);

    Task StopVmAsync(
        Guid vmId,
        CancellationToken cancellationToken);
}

public sealed record HyperVGuestIdentity(
    Guid VmId,
    Guid ServiceId,
    uint WindowsSessionId,
    Guid LoginInstanceId,
    string BootId,
    string UserSid,
    DateTimeOffset LoginTimeUtc);

public interface IHyperVGuestAgentProbe
{
    Task<HyperVGuestIdentity> ProbeAsync(
        Guid expectedVmId,
        CancellationToken cancellationToken);
}

public sealed class HyperVAgentEndpointRegistry
{
    private readonly ConcurrentDictionary<Guid, HyperVGuestIdentity>
        _endpoints = new();

    public void Set(Guid environmentId, HyperVGuestIdentity identity) =>
        _endpoints[environmentId] = identity;

    public bool TryGet(
        Guid environmentId,
        out HyperVGuestIdentity? identity) =>
        _endpoints.TryGetValue(environmentId, out identity);

    public void Remove(Guid environmentId) =>
        _endpoints.TryRemove(environmentId, out _);
}

public sealed class HyperVSessionBackend :
    IDesktopSessionBackend,
    IAsyncDisposable
{
    private readonly IHyperVHost _host;
    private readonly IHyperVGuestAgentProbe _guestProbe;
    private readonly HyperVAgentEndpointRegistry _endpoints;
    private readonly Func<Guid> _generationFactory;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Guid, CancellationToken, Task>? _showView;
    private readonly object _sync = new();
    private ActiveSession? _active;
    private bool _launching;
    private bool _disposed;

    public HyperVSessionBackend(
        IHyperVHost host,
        IHyperVGuestAgentProbe guestProbe,
        HyperVAgentEndpointRegistry? endpoints = null,
        Func<Guid>? generationFactory = null,
        TimeProvider? timeProvider = null,
        Func<Guid, CancellationToken, Task>? showView = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _guestProbe = guestProbe ??
            throw new ArgumentNullException(nameof(guestProbe));
        _endpoints = endpoints ?? new HyperVAgentEndpointRegistry();
        _generationFactory =
            generationFactory ?? (() => Guid.NewGuid());
        _timeProvider = timeProvider ?? TimeProvider.System;
        _showView = showView;
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

    public async Task<PreparationResult> PrepareAsync(
        EnvironmentProfile profile,
        SessionStartupPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var capabilities = CreateCapabilities();
        if (profile.Backend != DesktopSessionBackendKind.HyperV)
        {
            return Failed(capabilities, "UnsupportedBackend");
        }

        if (!TryParseVmId(profile, out var vmId))
            return Failed(capabilities, "InvalidVmId");

        var probe = await _host.ProbeAsync(vmId, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Available)
        {
            return Failed(
                capabilities,
                probe.ErrorCode ?? "HyperVUnavailable");
        }

        if (!probe.VmExists)
            return Failed(capabilities, "VmNotFound");

        if (policy == SessionStartupPolicy.ManagedOnly)
            return Failed(capabilities, "ManagedOnlyUnsupported");

        return new PreparationResult(
            true,
            RequiresNewLogin: true,
            capabilities,
            ErrorCode: null);
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

        if (profile.Backend != DesktopSessionBackendKind.HyperV)
        {
            throw new InvalidOperationException(
                "This backend only supports Hyper-V environments.");
        }

        if (!TryParseVmId(profile, out var vmId))
        {
            throw new InvalidOperationException(
                "The Hyper-V VM identity is invalid.");
        }

        lock (_sync)
        {
            if (_launching || _active is not null)
            {
                throw new InvalidOperationException(
                    "A Hyper-V desktop session is already active.");
            }

            _launching = true;
        }

        try
        {
            var probe = await _host.ProbeAsync(vmId, cancellationToken)
                .ConfigureAwait(false);
            if (!probe.Available)
            {
                throw new InvalidOperationException(
                    probe.ErrorCode ?? "HyperVUnavailable");
            }

            if (!probe.VmExists)
            {
                throw new InvalidOperationException(
                    "The selected Hyper-V VM does not exist.");
            }

            var powerState = await _host.GetPowerStateAsync(
                vmId,
                cancellationToken).ConfigureAwait(false);
            if (powerState != HyperVPowerState.Running)
            {
                await _host.StartVmAsync(vmId, cancellationToken)
                    .ConfigureAwait(false);
                powerState = await _host.GetPowerStateAsync(
                    vmId,
                    cancellationToken).ConfigureAwait(false);
            }

            if (powerState != HyperVPowerState.Running)
            {
                throw new InvalidOperationException(
                    "The Hyper-V VM did not reach the running state.");
            }

            if (_showView is not null)
                await _showView(vmId, cancellationToken).ConfigureAwait(false);

            var identity = await _guestProbe.ProbeAsync(
                vmId,
                cancellationToken).ConfigureAwait(false);
            ValidateIdentity(vmId, identity);
            cancellationToken.ThrowIfCancellationRequested();

            var generation = _generationFactory();
            if (generation == Guid.Empty)
                generation = Guid.NewGuid();
            var handle = new SessionHandle(
                profile.Id,
                generation,
                identity.WindowsSessionId,
                identity.LoginInstanceId,
                preparation.Succeeded);
            var active = new ActiveSession(
                handle,
                vmId,
                identity.ServiceId);

            lock (_sync)
            {
                ThrowIfDisposed();
                if (_active is not null)
                {
                    throw new InvalidOperationException(
                        "A Hyper-V desktop session is already active.");
                }

                _active = active;
            }

            _endpoints.Set(profile.Id, identity);
            active.Events.Writer.TryWrite(CreateEvent(
                handle,
                SessionBackendEventKind.Connected));
            return handle;
        }
        finally
        {
            lock (_sync)
                _launching = false;
        }
    }

    public Task ShowAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var active = GetActive(handle);
        return _showView?.Invoke(active.VmId, cancellationToken) ?? Task.CompletedTask;
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
                return;

            case SessionStopMode.Disconnect:
                await DisconnectAsync(active).ConfigureAwait(false);
                return;

            case SessionStopMode.ShutdownGuest:
                await _host.StopVmAsync(
                    active.VmId,
                    cancellationToken).ConfigureAwait(false);
                await DisconnectAsync(
                    active,
                    SessionBackendEventKind.SessionEnded)
                    .ConfigureAwait(false);
                return;

            case SessionStopMode.LogoffOwnedSession:
                throw new NotSupportedException(
                    "The Hyper-V backend cannot log off the guest account.");

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
            active = _active;
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
            await DisconnectAsync(active).ConfigureAwait(false);
    }

    private async Task DisconnectAsync(
        ActiveSession active,
        SessionBackendEventKind kind = SessionBackendEventKind.Disconnected)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, active))
                _active = null;
        }

        _endpoints.Remove(active.Handle.EnvironmentId);
        active.Events.Writer.TryWrite(CreateEvent(active.Handle, kind));
        active.Events.Writer.TryComplete();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private SessionBackendEvent CreateEvent(
        SessionHandle handle,
        SessionBackendEventKind kind) =>
        new(
            handle.EnvironmentId,
            handle.Generation,
            _timeProvider.GetUtcNow(),
            kind,
            handle.LoginInstanceId,
            Width: null,
            Height: null);

    private ActiveSession GetActive(SessionHandle handle)
    {
        lock (_sync)
        {
            var active = _active;
            if (active is null || active.Handle != handle)
            {
                throw new InvalidOperationException(
                    "The Hyper-V session handle is stale or not owned by this backend.");
            }

            return active;
        }
    }

    private static bool TryParseVmId(
        EnvironmentProfile profile,
        out Guid vmId) =>
        Guid.TryParse(profile.BackendProfileId, out vmId) &&
        vmId != Guid.Empty;

    private static void ValidateIdentity(
        Guid expectedVmId,
        HyperVGuestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.VmId != expectedVmId)
        {
            throw new InvalidOperationException(
                "The guest agent is bound to a different Hyper-V VM.");
        }

        if (identity.ServiceId == Guid.Empty ||
            identity.WindowsSessionId == 0 ||
            identity.LoginInstanceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(identity.BootId) ||
            string.IsNullOrWhiteSpace(identity.UserSid))
        {
            throw new InvalidOperationException(
                "The guest agent returned an invalid login identity.");
        }
    }

    private static SessionCapabilities CreateCapabilities() => new(
        CanRunHidden: false,
        CanSuppressStartupBeforeLaunch: false,
        SupportedStartupSources: ["EnvironmentDefault"],
        UnsupportedStartupSources:
        [
            "Hyper-V and guest-agent readiness must be verified before use.",
            "Hidden view rendering and input have not been validated.",
            "ManagedOnly startup suppression is unavailable."
        ]);

    private static PreparationResult Failed(
        SessionCapabilities capabilities,
        string errorCode) =>
        new(false, false, capabilities, errorCode);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class ActiveSession(
        SessionHandle handle,
        Guid vmId,
        Guid serviceId)
    {
        public SessionHandle Handle { get; } = handle;
        public Guid VmId { get; } = vmId;
        public Guid ServiceId { get; } = serviceId;
        public Channel<SessionBackendEvent> Events { get; } =
            Channel.CreateUnbounded<SessionBackendEvent>();
    }
}
