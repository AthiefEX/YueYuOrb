using FloatDesk.App.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class LatestSubtitleWorkSlotTests
{
    [Fact]
    public async Task LatestSubtitleWorkSlotDisposesReplacedPendingItem()
    {
        using var slot = new LatestSubtitleWorkSlot<DisposableItem>();
        var first = new DisposableItem();
        var second = new DisposableItem();

        slot.Publish(first);
        slot.Publish(second);

        var taken = await slot.TakeAsync(CancellationToken.None);

        Assert.Same(second, taken);
        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
        taken!.Dispose();
    }

    [Fact]
    public async Task DisposingSlotReleasesWaitingTakeWithNull()
    {
        var slot = new LatestSubtitleWorkSlot<DisposableItem>();
        var waiting = slot.TakeAsync(CancellationToken.None);

        slot.Dispose();

        Assert.Null(await waiting);
    }

    private sealed class DisposableItem : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
