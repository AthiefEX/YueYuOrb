using FloatDesk.App.Brightness;

namespace FloatDesk.Ui.Tests;

public sealed class NightModeStateTests
{
    [Fact]
    public void NestedSuspensionAndDisableCannotRestoreOldIntent()
    {
        var state = new NightModeState();
        Assert.False(state.IsVisible);
        state.SetEnabled(true);
        Assert.True(state.IsVisible);
        var first = state.Suspend();
        var second = state.Suspend();
        first.Dispose(); first.Dispose();
        Assert.False(state.IsVisible);
        state.SetEnabled(false);
        second.Dispose();
        Assert.False(state.IsVisible);
        state.SetEnabled(true);
        Assert.True(state.IsVisible);
    }

    [Fact]
    public void DimmingIsBoundedAndNewIntentStaysSuspended()
    {
        var state = new NightModeState();
        using var lease = state.Suspend();
        state.SetEnabled(true);
        state.SetDimmingPercent(100);
        Assert.Equal(90, state.DimmingPercent);
        Assert.False(state.IsVisible);
        state.SetDimmingPercent(-1);
        Assert.Equal(0, state.DimmingPercent);
    }
}
