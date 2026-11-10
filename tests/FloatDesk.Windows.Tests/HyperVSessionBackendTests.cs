using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Globalization;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVSessionBackendTests
{
    [Fact]
    public async Task ShowInvokesViewForTheAuthenticatedVm()
    {
        Guid? shownVm = null;
        await using var backend = new HyperVSessionBackend(new FakeHost(), new FakeGuestProbe { Identity = ValidIdentity() },
            showView: (vmId, token) => { token.ThrowIfCancellationRequested(); shownVm = vmId; return Task.CompletedTask; });
        var profile = CreateProfile();
        var preparation = await backend.PrepareAsync(profile, profile.StartupPolicy, CancellationToken.None);
        var handle = await backend.StartAsync(profile, preparation, CancellationToken.None);
        Assert.Equal(VmId, shownVm);
        await backend.ShowAsync(handle, CancellationToken.None);
        Assert.Equal(VmId, shownVm);
    }
    private static readonly Guid VmId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ServiceId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task WrongVmRejected()
    {
        var host = new FakeHost();
        var probe = new FakeGuestProbe
        {
            Identity = ValidIdentity() with
            {
                VmId = Guid.Parse(
                    "cccccccc-cccc-cccc-cccc-cccccccccccc")
            }
        };
        await using var backend = new HyperVSessionBackend(host, probe);
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
    }

    [Fact]
    public void BadPairingRejected()
    {
        var handshake = new HyperVPairingHandshake();
        var hello = HyperVPairingProtocol.CreateClientHello(
            VmId,
            ServiceId);
        var server = HyperVPairingProtocol.CreateServerHello(
            Enumerable.Repeat((byte)0x21, 32).ToArray(),
            hello);

        Assert.False(handshake.VerifyServerHello(
            Enumerable.Repeat((byte)0x42, 32).ToArray(),
            hello,
            server));
    }

    [Fact]
    public void ReplayedHandshakeRejected()
    {
        var secret = Enumerable.Repeat((byte)0x21, 32).ToArray();
        var handshake = new HyperVPairingHandshake();
        var hello = HyperVPairingProtocol.CreateClientHello(
            VmId,
            ServiceId);
        var server = HyperVPairingProtocol.CreateServerHello(
            secret,
            hello);

        Assert.True(handshake.VerifyServerHello(secret, hello, server));
        Assert.False(handshake.VerifyServerHello(secret, hello, server));
    }

    [Fact]
    public async Task PreexistingVmNotPoweredOff()
    {
        var host = new FakeHost
        {
            PowerState = HyperVPowerState.Running
        };
        var probe = new FakeGuestProbe
        {
            Identity = ValidIdentity()
        };
        await using var backend = new HyperVSessionBackend(host, probe);
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
            SessionStopMode.Disconnect,
            CancellationToken.None);
        await backend.DisposeAsync();

        Assert.Equal(0, host.StartCount);
        Assert.Equal(0, host.StopCount);
    }

    [Fact]
    public async Task MissingHyperVExplainsUnavailable()
    {
        var host = new FakeHost
        {
            ProbeResult = new HyperVHostProbe(
                Available: false,
                VmExists: false,
                ErrorCode: "HyperVUnavailable")
        };
        await using var backend = new HyperVSessionBackend(
            host,
            new FakeGuestProbe());

        var preparation = await backend.PrepareAsync(
            CreateProfile(),
            SessionStartupPolicy.EnvironmentDefault,
            CancellationToken.None);

        Assert.False(preparation.Succeeded);
        Assert.Equal("HyperVUnavailable", preparation.ErrorCode);
        Assert.Contains(
            preparation.Capabilities.UnsupportedStartupSources,
            reason => reason.Contains(
                "Hyper-V",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ViewDisconnectDoesNotClaimTaskReady()
    {
        var host = new FakeHost();
        var probe = new FakeGuestProbe
        {
            Identity = ValidIdentity()
        };
        await using var backend = new HyperVSessionBackend(host, probe);
        var profile = CreateProfile();
        var preparation = await backend.PrepareAsync(
            profile,
            SessionStartupPolicy.EnvironmentDefault,
            CancellationToken.None);
        var handle = await backend.StartAsync(
            profile,
            preparation,
            CancellationToken.None);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        await using var events = backend.WatchAsync(
            profile.Id,
            timeout.Token).GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(
            SessionBackendEventKind.Connected,
            events.Current.Kind);

        var disconnected = events.MoveNextAsync().AsTask();
        await backend.StopAsync(
            handle,
            SessionStopMode.Disconnect,
            CancellationToken.None);

        Assert.True(await disconnected);
        Assert.Equal(
            SessionBackendEventKind.Disconnected,
            events.Current.Kind);
        Assert.NotEqual(
            SessionBackendEventKind.Connected,
            events.Current.Kind);
    }

    [Fact]
    public async Task PairingSecretUsesProtectedStore()
    {
        var protector = new XorProtector();
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"YueYuOrb-HyperV-{Guid.NewGuid():N}");
        try
        {
            var store = new DpapiHyperVPairingSecretStore(
                directory,
                protector);
            var secret = Enumerable.Range(1, 32)
                .Select(value => (byte)value)
                .ToArray();

            await store.SaveAsync(VmId, secret, CancellationToken.None);
            var loaded = await store.LoadAsync(
                VmId,
                CancellationToken.None);

            Assert.Equal(secret, loaded);
            var bytes = await File.ReadAllBytesAsync(
                Assert.Single(Directory.GetFiles(directory)));
            Assert.DoesNotContain(
                Convert.ToHexString(secret),
                Convert.ToHexString(bytes));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static HyperVGuestIdentity ValidIdentity() => new(
        VmId,
        ServiceId,
        WindowsSessionId: 3,
        LoginInstanceId:
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
        BootId: "boot-1",
        UserSid: "S-1-5-21-1000",
        LoginTimeUtc: DateTimeOffset.Parse(
            "2026-11-06T01:02:03Z",
            CultureInfo.InvariantCulture));

    private static EnvironmentProfile CreateProfile() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Hyper-V 测试环境",
        DesktopSessionBackendKind.HyperV,
        VmId.ToString("D"),
        SessionStartupPolicy.EnvironmentDefault,
        new SessionLimits(),
        []);

    private sealed class FakeHost : IHyperVHost
    {
        public HyperVHostProbe ProbeResult { get; set; } =
            new(true, true, null);
        public HyperVPowerState PowerState { get; set; } =
            HyperVPowerState.Off;
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public Task<HyperVHostProbe> ProbeAsync(
            Guid vmId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ProbeResult);

        public Task<HyperVPowerState> GetPowerStateAsync(
            Guid vmId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PowerState);

        public Task StartVmAsync(
            Guid vmId,
            CancellationToken cancellationToken)
        {
            StartCount++;
            PowerState = HyperVPowerState.Running;
            return Task.CompletedTask;
        }

        public Task StopVmAsync(
            Guid vmId,
            CancellationToken cancellationToken)
        {
            StopCount++;
            PowerState = HyperVPowerState.Off;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGuestProbe : IHyperVGuestAgentProbe
    {
        public HyperVGuestIdentity Identity { get; set; } =
            ValidIdentity();

        public Task<HyperVGuestIdentity> ProbeAsync(
            Guid expectedVmId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Identity);
    }

    private sealed class XorProtector : IDesktopSessionDataProtector
    {
        public byte[] Protect(byte[] data) =>
            data.Select(value => (byte)(value ^ 0xA5)).ToArray();

        public byte[] Unprotect(byte[] data) =>
            Protect(data);
    }
}
