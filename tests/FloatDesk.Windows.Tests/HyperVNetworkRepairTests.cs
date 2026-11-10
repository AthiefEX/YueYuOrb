using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVNetworkRepairTests
{
    [Fact]
    public void ProxyProbeDnsFailureDoesNotPretendInternetIsUnreachable()
    {
        var status = new HyperVNetworkStatus(true, true, true, true, false);
        Assert.True(status.Reachable);
        Assert.False(status.Healthy);
        Assert.Contains("Fake-IP", status.Summary);
    }
    [Fact]
    public async Task HealthyNetworkDoesNotDisconnect()
    {
        var ops = new FakeOperations();
        var result = await new HyperVNetworkRepair(ops).RepairAsync(Guid.NewGuid(), Guid.NewGuid(), null, default);
        Assert.True(result.Internet);
        Assert.Equal(0, ops.Reconnects);
    }

    [Fact]
    public async Task StaleProfileTriggersReconnectAndFreshVerificationForSameVm()
    {
        var vm = Guid.NewGuid(); var env = Guid.NewGuid();
        var ops = new FakeOperations { Stale = true };
        var result = await new HyperVNetworkRepair(ops).RepairAsync(vm, env, null, default);
        Assert.True(result.Internet);
        Assert.Equal(1, ops.Reconnects);
        Assert.All(ops.Targets, target => Assert.Equal((vm, env), target));
    }

    [Fact]
    public async Task InternetAccessDoesNotPretendTheWindowsProfileIsRepaired()
    {
        var ops = new FakeOperations { Stale = true, RemainStale = true };
        var result = await new HyperVNetworkRepair(ops).RepairAsync(Guid.NewGuid(), Guid.NewGuid(), null, default);
        Assert.True(result.Reachable);
        Assert.False(result.Internet);
    }

    [Fact]
    public async Task CancellationWaitsForReconnectCleanup()
    {
        using var cancel = new CancellationTokenSource();
        var ops = new FakeOperations { Stale = true, DuringReconnect = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HyperVNetworkRepair(ops).RepairAsync(Guid.NewGuid(), Guid.NewGuid(), null, cancel.Token));
        Assert.True(ops.ReconnectCompleted);
        Assert.False(ops.ReconnectTokenCanCancel);
    }

    [Fact]
    public async Task InvalidIdentityDoesNotTouchNetwork()
    {
        var ops = new FakeOperations();
        await Assert.ThrowsAsync<ArgumentException>(() => new HyperVNetworkRepair(ops).RepairAsync(Guid.Empty, Guid.NewGuid(), null, default));
        Assert.Empty(ops.Targets);
    }

    private sealed class FakeOperations : IHyperVNetworkOperations
    {
        public bool Stale, RemainStale, ReconnectCompleted, ReconnectTokenCanCancel;
        public int Reconnects;
        public Action? DuringReconnect;
        public List<(Guid, Guid)> Targets { get; } = [];
        public Task<HyperVNetworkStatus> CheckAsync(Guid vm, Guid env, CancellationToken token)
        { Targets.Add((vm, env)); return Task.FromResult(new HyperVNetworkStatus(true, true, true, !Stale)); }
        public Task FlushDnsAsync(Guid vm, Guid env, CancellationToken token)
        { Targets.Add((vm, env)); return Task.CompletedTask; }
        public Task ReconnectAsync(Guid vm, CancellationToken token)
        {
            Reconnects++; ReconnectTokenCanCancel = token.CanBeCanceled;
            DuringReconnect?.Invoke(); ReconnectCompleted = true; Stale = RemainStale;
            return Task.CompletedTask;
        }
    }
}
