using FloatDesk.App.Coordination;

namespace FloatDesk.Ui.Tests;

public sealed class CaptureSessionGateTests
{
    [Fact]
    public async Task GateAllowsOnlyOneHolderUntilReleased()
    {
        using var gate = new CaptureSessionGate();

        Assert.True(await gate.EnterAsync(CancellationToken.None));
        Assert.False(await gate.EnterAsync(CancellationToken.None));

        gate.Release();

        Assert.True(await gate.EnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledAcquireDoesNotDisturbCurrentHolder()
    {
        using var gate = new CaptureSessionGate();
        Assert.True(await gate.EnterAsync(CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.EnterAsync(cancellation.Token));

        gate.Release();
        Assert.True(await gate.EnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DisposeRejectsNewAcquiresAndLateReleaseRemainsSafe()
    {
        var gate = new CaptureSessionGate();
        Assert.True(await gate.EnterAsync(CancellationToken.None));

        gate.Dispose();
        gate.Release();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            gate.EnterAsync(CancellationToken.None));
    }
}
