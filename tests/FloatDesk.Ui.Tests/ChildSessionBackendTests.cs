using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Ui.Tests;

public sealed class ChildSessionBackendTests
{
    [Fact]
    public async Task ExistingForeignChildSessionNotTakenOver()
    {
        var native = new FakeNative
        {
            Enabled = true,
            ChildSessionId = 77
        };
        var launcher = new FakeLauncher();
        var backend = CreateBackend(native, launcher);
        var profile = CreateProfile();
        var preparation = await backend.PrepareAsync(
            profile,
            SessionStartupPolicy.EnvironmentDefault,
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.StartAsync(
                profile,
                preparation,
                CancellationToken.None));

        Assert.Equal(0, launcher.LaunchCount);
        Assert.Equal(0, native.LogoffCount);
    }

    [Fact]
    public async Task ParentSessionNeverLoggedOff()
    {
        var native = new FakeNative
        {
            Enabled = true
        };
        var launcher = new FakeLauncher
        {
            SessionId = 88
        };
        var backend = CreateBackend(native, launcher);
        var profile = CreateProfile();
        var preparation = await backend.PrepareAsync(
            profile,
            SessionStartupPolicy.EnvironmentDefault,
            CancellationToken.None);
        var handle = await backend.StartAsync(
            profile,
            preparation,
            CancellationToken.None);

        await backend.StopAsync(
            handle,
            SessionStopMode.HideView,
            CancellationToken.None);
        await backend.StopAsync(
            handle,
            SessionStopMode.Disconnect,
            CancellationToken.None);

        Assert.Equal(0, native.LogoffCount);
        Assert.Equal(1, launcher.HideCount);
        Assert.Equal(1, launcher.DisconnectCount);
    }

    [Fact]
    public async Task HiddenCapabilityMustBeMeasured()
    {
        var native = new FakeNative
        {
            Enabled = true
        };
        var backend = CreateBackend(native, new FakeLauncher());

        var capabilities = await backend.ProbeAsync(
            CreateProfile(),
            CancellationToken.None);

        Assert.False(capabilities.CanRunHidden);
        Assert.Contains(
            capabilities.UnsupportedStartupSources,
            reason => reason.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CancelStartDoesNotTerminateForeignSession()
    {
        var native = new FakeNative
        {
            Enabled = true
        };
        var launcher = new FakeLauncher
        {
            SessionId = 99,
            ConnectGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously),
            HonorCancellation = true
        };
        var backend = CreateBackend(native, launcher);
        var profile = CreateProfile();
        var preparation = await backend.PrepareAsync(
            profile,
            SessionStartupPolicy.EnvironmentDefault,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var start = backend.StartAsync(
            profile,
            preparation,
            cancellation.Token);
        await launcher.LaunchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        native.ChildSessionId = 123;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.Equal(0, native.LogoffCount);
        Assert.Null(launcher.LastBehavior);
    }

    [Fact]
    public async Task FailedConnectDisposesCom()
    {
        var control = new FakeRdpControl
        {
            FailConnect = true
        };
        await using var host = new RdpChildSessionHost(
            new FakeRdpControlFactory(control));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.ConnectAsync(
                width: 1280,
                height: 720,
                CancellationToken.None));

        Assert.True(control.Disposed);
        Assert.False(host.IsConnected);
    }

    private static ChildSessionBackend CreateBackend(
        FakeNative native,
        FakeLauncher launcher) =>
        new(
            native,
            launcher,
            loginInstanceFactory: () =>
                Guid.Parse("44444444-4444-4444-4444-444444444444"));

    private static EnvironmentProfile CreateProfile() =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "本机子会话",
            DesktopSessionBackendKind.LocalChild,
            "local-child",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);

    private sealed class FakeNative : IChildSessionNative
    {
        public bool Enabled { get; set; }
        public uint? ChildSessionId { get; set; }
        public int LogoffCount { get; private set; }
        public bool SupportsChildSessions => true;

        public bool TryGetEnabled(out bool enabled)
        {
            enabled = Enabled;
            return true;
        }

        public bool TrySetEnabled(bool enabled)
        {
            Enabled = enabled;
            return true;
        }

        public bool TryGetChildSessionId(out uint sessionId)
        {
            sessionId = ChildSessionId ?? uint.MaxValue;
            return true;
        }

        public bool TryLogoffSession(uint sessionId)
        {
            LogoffCount++;
            return true;
        }
    }

    private sealed class FakeLauncher : IChildSessionLauncher
    {
        public uint SessionId { get; set; } = 88;
        public int LaunchCount { get; private set; }
        public int HideCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public string? LastBehavior { get; private set; }
        public TaskCompletionSource? ConnectGate { get; set; }
        public bool HonorCancellation { get; set; }
        public TaskCompletionSource LaunchEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChildSessionLaunch> LaunchAsync(
            EnvironmentProfile profile,
            CancellationToken cancellationToken)
        {
            LaunchCount++;
            LaunchEntered.TrySetResult();
            if (ConnectGate is not null)
            {
                if (HonorCancellation)
                {
                    await ConnectGate.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    await ConnectGate.Task;
                }
            }

            return new ChildSessionLaunch(
                SessionId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new NoopConnection());
        }

        public Task ShowAsync(SessionHandle handle, CancellationToken cancellationToken)
        {
            LastBehavior = "show";
            return Task.CompletedTask;
        }

        public Task HideAsync(SessionHandle handle, CancellationToken cancellationToken)
        {
            HideCount++;
            LastBehavior = "hide";
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(SessionHandle handle, CancellationToken cancellationToken)
        {
            DisconnectCount++;
            LastBehavior = "disconnect";
            return Task.CompletedTask;
        }
    }

    private sealed class NoopConnection : IChildSessionConnection
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record FakeRdpControlFactory(FakeRdpControl Control) :
        IRdpControlFactory
    {
        public IRdpControl Create() => Control;
    }

    private sealed class FakeRdpControl : IRdpControl
    {
        public bool FailConnect { get; set; }
        public bool Disposed { get; private set; }
        public bool IsConnected { get; private set; }

        public Task ConfigureAsync(
            int width,
            int height,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Connect()
        {
            if (FailConnect)
                throw new InvalidOperationException("connect failed");
            IsConnected = true;
        }

        public void Disconnect() => IsConnected = false;

        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
        }
    }
}
