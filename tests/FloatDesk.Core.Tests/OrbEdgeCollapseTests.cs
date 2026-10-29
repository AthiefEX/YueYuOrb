using FloatDesk.Core.Geometry;

namespace FloatDesk.Core.Tests;

public sealed class OrbEdgeCollapseTests
{
    [Fact]
    public void CollapsesOnlyAfterDelayAndRestoresOnHover()
    {
        var state = new OrbEdgeCollapseState();
        Assert.False(state.Update(true, 1, false, false, TimeSpan.Zero));
        Assert.False(state.Update(true, 1, false, false, TimeSpan.FromMilliseconds(799)));
        Assert.True(state.Update(true, 1, false, false, TimeSpan.FromMilliseconds(800)));
        Assert.False(state.Update(true, 1, true, false, TimeSpan.FromMilliseconds(900)));
        Assert.False(state.Update(true, 1, false, false, TimeSpan.FromMilliseconds(1000)));
        Assert.False(state.Update(true, 1, false, false, TimeSpan.FromMilliseconds(1799)));
        Assert.True(state.Update(true, 1, false, false, TimeSpan.FromMilliseconds(1800)));
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    public void DisabledDetachedOrBlockedAlwaysRestores(bool enabled, int edge, bool blocked)
    {
        var state = new OrbEdgeCollapseState();
        state.Update(true, 1, false, false, TimeSpan.Zero);
        Assert.True(state.Update(true, 1, false, false, TimeSpan.FromSeconds(1)));
        Assert.False(state.Update(enabled, edge, false, blocked, TimeSpan.FromSeconds(2)));
    }
}
