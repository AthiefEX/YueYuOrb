using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVSocketListenerTests
{
    [Fact]
    public void RequiresStableServiceIdentity()
    {
        Assert.Throws<ArgumentException>(() => new WindowsHyperVSocketListener(Guid.Empty));
    }

    [Fact]
    public async Task CannotAcceptBeforeStart()
    {
        using var cancellation = new CancellationTokenSource();
        await using var listener = new WindowsHyperVSocketListener(Guid.NewGuid());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            listener.AcceptLoopAsync((_, _) => Task.CompletedTask, cancellation.Token));
        Assert.Equal("The Hyper-V socket listener is not started.", error.Message);
    }
}
