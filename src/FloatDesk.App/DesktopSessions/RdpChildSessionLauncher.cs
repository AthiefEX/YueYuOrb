using System.Collections.Concurrent;
using System.Windows.Threading;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

public sealed class RdpChildSessionLauncher : IChildSessionLauncher
{
    private const uint NoChildSession = uint.MaxValue;
    private static readonly TimeSpan ChildSessionStartTimeout =
        TimeSpan.FromSeconds(30);

    private readonly IChildSessionNative _native;
    private readonly IRdpControlFactory _controlFactory;
    private readonly Func<uint> _parentSessionIdProvider;
    private readonly IChildSessionAgentBootstrapper? _agentBootstrapper;
    private readonly ChildSessionAgentBootstrapStore? _bootstrapStore;
    private readonly ConcurrentDictionary<uint, RdpSessionView> _views = [];

    public RdpChildSessionLauncher(
        IChildSessionNative native,
        IRdpControlFactory? controlFactory = null,
        Func<uint>? parentSessionIdProvider = null,
        IChildSessionAgentBootstrapper? agentBootstrapper = null,
        ChildSessionAgentBootstrapStore? bootstrapStore = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _controlFactory = controlFactory ?? new RdpControlFactory();
        _parentSessionIdProvider =
            parentSessionIdProvider ?? GetCurrentProcessSessionId;
        if ((agentBootstrapper is null) != (bootstrapStore is null))
        {
            throw new ArgumentException(
                "The agent bootstrapper and bootstrap store must be supplied together.");
        }

        _agentBootstrapper = agentBootstrapper;
        _bootstrapStore = bootstrapStore;
    }

    public async Task<ChildSessionLaunch> LaunchAsync(
        EnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var parentSessionId = _parentSessionIdProvider();
        if (TryGetChildSessionId(out var existingSessionId))
        {
            throw new InvalidOperationException(
                $"Child session {existingSessionId} already exists and is not owned by this launcher.");
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher ??
            Dispatcher.CurrentDispatcher;
        var generation = Guid.NewGuid();
        RdpSessionView? view = null;
        try
        {
            view = await dispatcher.InvokeAsync(
                () => CreateView(profile),
                DispatcherPriority.Normal,
                cancellationToken);
            await view.ShowAsync(cancellationToken).ConfigureAwait(false);
            await view.ConnectAsync(
                width: 1280,
                height: 720,
                cancellationToken).ConfigureAwait(false);
            var sessionId = await WaitForChildSessionAsync(
                parentSessionId,
                cancellationToken).ConfigureAwait(false);
            view.SessionId = sessionId;
            var loginInstanceId = Guid.Empty;
            if (_agentBootstrapper is not null)
            {
                var bootstrap = await _agentBootstrapper.LaunchAsync(
                    profile.Id,
                    generation,
                    sessionId,
                    cancellationToken).ConfigureAwait(false);
                loginInstanceId = bootstrap.LoginInstanceId;
                _bootstrapStore!.Set(
                    new SessionHandle(
                        profile.Id,
                        generation,
                        sessionId,
                        bootstrap.LoginInstanceId,
                        PreparationCompleted: true),
                    bootstrap);
            }

            if (!_views.TryAdd(sessionId, view))
            {
                if (_bootstrapStore is not null)
                {
                    _bootstrapStore.Remove(new SessionHandle(
                        profile.Id,
                        generation,
                        sessionId,
                        loginInstanceId,
                        PreparationCompleted: true));
                }

                throw new InvalidOperationException(
                    "Another child-session view owns the new session.");
            }

            return new ChildSessionLaunch(
                sessionId,
                generation,
                loginInstanceId,
                view);
        }
        catch
        {
            if (view is not null)
                await view.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ShowAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        await GetView(handle).ShowAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HideAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        await GetView(handle).HideAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DisconnectAsync(
        SessionHandle handle,
        CancellationToken cancellationToken)
    {
        var view = GetView(handle);
        await view.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        _views.TryRemove(handle.WindowsSessionId, out _);
    }

    private RdpSessionView CreateView(EnvironmentProfile profile)
    {
        var control = _controlFactory.Create();
        var host = new RdpChildSessionHost(control);
        var window = new DesktopSessionWindow(
            control,
            profile.Name);
        return new RdpSessionView(window, host, window.Dispatcher);
    }

    private async Task<uint> WaitForChildSessionAsync(
        uint parentSessionId,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            ChildSessionStartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (TryGetChildSessionId(out var sessionId) &&
                sessionId != parentSessionId)
            {
                return sessionId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token)
                .ConfigureAwait(false);
        }
    }

    private bool TryGetChildSessionId(out uint sessionId) =>
        _native.TryGetChildSessionId(out sessionId) &&
        sessionId is not (0 or NoChildSession);

    private RdpSessionView GetView(SessionHandle handle)
    {
        if (_views.TryGetValue(
                handle.WindowsSessionId,
                out var view) &&
            view.SessionId == handle.WindowsSessionId)
        {
            return view;
        }

        throw new InvalidOperationException(
            "The child-session view is not active.");
    }

    private static uint GetCurrentProcessSessionId()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return unchecked((uint)process.SessionId);
    }

    private sealed class RdpSessionView(
        DesktopSessionWindow window,
        RdpChildSessionHost host,
        Dispatcher dispatcher) : IChildSessionConnection
    {
        private int _disconnectStarted;
        private int _disposed;

        public uint SessionId { get; set; }

        public Task ConnectAsync(
            int width,
            int height,
            CancellationToken cancellationToken) =>
            host.ConnectAsync(width, height, cancellationToken);

        public Task ShowAsync(CancellationToken cancellationToken) =>
            dispatcher.InvokeAsync(
                () =>
                {
                    window.ShowActivated = false;
                    if (!window.IsVisible)
                        window.Show();
                },
                DispatcherPriority.Normal,
                cancellationToken).Task;

        public Task HideAsync(CancellationToken cancellationToken) =>
            dispatcher.InvokeAsync(
                () =>
                {
                    if (window.IsVisible)
                        window.Hide();
                },
                DispatcherPriority.Normal,
                cancellationToken).Task;

        public async Task DisconnectAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disconnectStarted, 1) != 0)
                return;

            await dispatcher.InvokeAsync(
                () =>
                {
                    try
                    {
                        host.Disconnect();
                    }
                    finally
                    {
                        if (window.IsVisible)
                            window.Hide();
                    }
                },
                DispatcherPriority.Normal,
                cancellationToken).Task.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await DisconnectAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        window.AllowClose();
                        window.Close();
                        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    },
                    DispatcherPriority.Normal).Task.ConfigureAwait(false);
            }
        }
    }
}
