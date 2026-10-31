using FloatDesk.Core.Danmaku;
namespace FloatDesk.Core.Tests;

public sealed class NotificationReadGateTests
{
    [Fact]
    public void NotificationThatDisappearsBeforeReadDoesNotCreateAnIdleRetryLoop()
    {
        var gate = new NotificationReadGate();
        Assert.True(gate.TryBeginRead()); gate.Complete([1u]);
        gate.Added(2); Assert.True(gate.TryBeginRead());
        gate.Complete([1u]);
        Assert.False(gate.HasPending);
        Assert.False(gate.TryBeginRead());
    }
    [Fact]
    public void IdleTicksAndRepeatedExistingEventsNeverReenumerateNotifications()
    {
        var gate = new NotificationReadGate();
        Assert.True(gate.TryBeginRead());
        gate.Complete([1u]);
        for (var i = 0; i < 100; i++)
        {
            gate.Added(1);
            Assert.False(gate.TryBeginRead());
        }
        gate.Added(2); gate.Added(2);
        Assert.True(gate.TryBeginRead());
        Assert.False(gate.TryBeginRead());
        gate.Complete([1u, 2u]);
        Assert.False(gate.TryBeginRead());
    }
    [Fact]
    public void NewEventsDuringReadSurviveAndRemovedIdentitiesMayBeReused()
    {
        var gate = new NotificationReadGate();
        Assert.True(gate.TryBeginRead());
        gate.Added(2); gate.Complete([1u]);
        Assert.True(gate.TryBeginRead()); gate.Complete([1u, 2u]);
        gate.Removed(2); gate.Added(2);
        Assert.True(gate.TryBeginRead()); gate.Complete([1u, 2u]);
        gate.Reset(); Assert.True(gate.TryBeginRead());
    }
}
