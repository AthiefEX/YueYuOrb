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
}
