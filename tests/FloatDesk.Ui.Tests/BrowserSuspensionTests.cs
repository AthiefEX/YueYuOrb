#pragma warning disable CA2201 // Deliberately simulate WebView2 COM teardown failures.
using System.Runtime.InteropServices;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

public sealed class BrowserSuspensionTests
{
    [Fact]
    public async Task DisposedStateDuringStatusReadIsContained()
    {
        var outcome = await BrowserSuspension.TrySuspendAsync(
            () => throw new InvalidOperationException("disposed"),
            () => Task.FromResult(true));

        Assert.Equal(BrowserSuspendOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task ComFailureDuringSuspendIsContained()
    {
        var outcome = await BrowserSuspension.TrySuspendAsync(
            () => false,
            () => throw ComFailure());

        Assert.Equal(BrowserSuspendOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task AlreadySuspendedBrowserIsNotSuspendedAgain()
    {
        var suspendCalled = false;
        var outcome = await BrowserSuspension.TrySuspendAsync(
            () => true,
            () =>
            {
                suspendCalled = true;
                return Task.FromResult(true);
            });

        Assert.Equal(BrowserSuspendOutcome.AlreadySuspended, outcome);
        Assert.False(suspendCalled);
    }

    private static COMException ComFailure() => new("unavailable");
}

#pragma warning restore CA2201
