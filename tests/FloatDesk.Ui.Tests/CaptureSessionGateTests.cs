using FloatDesk.App.Coordination;

namespace FloatDesk.Ui.Tests;

public sealed class CaptureSessionGateTests
{
    [Fact]
    public void TryEnterAllowsOnlyOneLiveLease()
    {
        using var gate = new CaptureSessionGate();
        using var first = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());

        Assert.Null(gate.TryEnter());

        first.Dispose();

        using var second = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        Assert.NotNull(second);
    }

    [Fact]
    public void LeaseDisposalIsOwnerOnlyIdempotentAndCannotReleaseSuccessor()
    {
        using var gate = new CaptureSessionGate();
        var first = Assert.IsType<CaptureSessionGate.Lease>(gate.TryEnter());
        first.Dispose();
        first.Dispose();
        using var second = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());

        first.Dispose();

        Assert.Null(gate.TryEnter());
        second.Dispose();
        using var third = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        Assert.NotNull(third);
    }

    [Fact(Timeout = 10_000)]
    public async Task PendingWaiterReceivesLeaseAfterOwnerReleases()
    {
        using var gate = new CaptureSessionGate();
        using var first = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        var waiting = gate.EnterAsync(CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(gate.TryEnter());
        second.Dispose();
    }

    [Fact]
    public async Task CancelledWaiterDoesNotDisturbCurrentHolderOrLaterWaiter()
    {
        using var gate = new CaptureSessionGate();
        using var first = Assert.IsType<CaptureSessionGate.Lease>(
            gate.TryEnter());
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(cancellation.Token);
        var later = gate.EnterAsync(CancellationToken.None);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancelled);

        first.Dispose();
        using var second = await later.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(gate.TryEnter());
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposeCompletesWaitersAndInvalidatesExistingLease()
    {
        var gate = new CaptureSessionGate();
        var lease = Assert.IsType<CaptureSessionGate.Lease>(gate.TryEnter());
        var waiter = gate.EnterAsync(CancellationToken.None);

        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => gate.TryEnter());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            gate.EnterAsync(CancellationToken.None));
    }
}
