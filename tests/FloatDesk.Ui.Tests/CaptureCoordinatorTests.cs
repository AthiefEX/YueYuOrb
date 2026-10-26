using FloatDesk.App.Coordination;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class CaptureCoordinatorTests
{
    [Fact(Timeout = 10_000)]
    public async Task SharedGateOwnershipProducesCaptureBusyWithoutStartingSession()
    {
        using var gate = new CaptureSessionGate();
        using var held = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        using var coordinator = new CaptureCoordinator(
            null!,
            null!,
            () => AppSettings.CreateDefault(),
            static () => 0,
            static _ => { },
            sessionGate: gate);

        var result = await coordinator.StartAsync(
            CaptureMode.Region,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("CaptureBusy", result.ErrorCode);

        held.Dispose();
        using var replacement = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        Assert.NotNull(replacement);
    }

    [Fact(Timeout = 10_000)]
    public async Task ActiveFlagClearsBeforeLeaseAllowsSuccessor()
    {
        using var gate = new CaptureSessionGate();
        var states = new List<bool>();
        var successorCouldEnterDuringCleanup = false;
        using var coordinator = new CaptureCoordinator(
            null!,
            null!,
            () => AppSettings.CreateDefault(),
            static () => 0,
            active =>
            {
                states.Add(active);
                if (active)
                    return;
                using var probe = gate.TryEnter();
                successorCouldEnterDuringCleanup = probe is not null;
            },
            sessionGate: gate);

        var result = await coordinator.StartAsync(
            CaptureMode.Region,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal([true, false], states);
        Assert.False(successorCouldEnterDuringCleanup);
        using var next = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        Assert.NotNull(next);
    }

    [Fact]
    public void DisposeOnlyDisposesRegionSelectionItCreated()
    {
        using var firstGate = new CaptureSessionGate();
        var injected = new RegionSelectionCoordinator();
        var injectedOwner = new CaptureCoordinator(
            null!,
            null!,
            () => AppSettings.CreateDefault(),
            static () => 0,
            static _ => { },
            sessionGate: firstGate,
            regionSelection: injected);

        injectedOwner.Dispose();

        Assert.False(injected.IsDisposed);
        injected.Dispose();

        using var secondGate = new CaptureSessionGate();
        var ownedOwner = new CaptureCoordinator(
            null!,
            null!,
            () => AppSettings.CreateDefault(),
            static () => 0,
            static _ => { },
            sessionGate: secondGate);
        var owned = ownedOwner.RegionSelection;

        ownedOwner.Dispose();

        Assert.True(owned.IsDisposed);
    }
}
