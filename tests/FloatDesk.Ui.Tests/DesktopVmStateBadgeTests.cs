using FloatDesk.App.Views;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopVmStateBadgeTests
{
    [Theory]
    [InlineData(HyperVVirtualMachineState.Running, "运行中", 0xFF3CB371u)]
    [InlineData(HyperVVirtualMachineState.Saved, "已保存，尚未启动", 0xFF1E90FFu)]
    [InlineData(HyperVVirtualMachineState.Off, "已关闭", 0xFFCD5C5Cu)]
    [InlineData(HyperVVirtualMachineState.Paused, "已暂停", 0xFFDAA520u)]
    public void KnownStateReportsItsOwnColorAndWording(HyperVVirtualMachineState state, string text, uint argb)
    {
        var badge = DesktopVmStateBadges.For(true, state, TimeSpan.FromSeconds(1));
        Assert.Equal(text, badge.Text);
        Assert.Equal(argb, badge.Argb);
    }

    [Fact]
    public void AClosedMachineNeverKeepsTheRunningDot()
    {
        // The report: the console was closed (host state Off) but the environment list still showed
        // a green "running" dot. Anything but a live snapshot of Running must not be green.
        Assert.NotEqual(DesktopVmStateBadges.For(true, HyperVVirtualMachineState.Running, TimeSpan.FromSeconds(1)).Argb,
            DesktopVmStateBadges.For(true, HyperVVirtualMachineState.Off, TimeSpan.FromSeconds(1)).Argb);
    }

    [Fact]
    public void AStaleSnapshotStopsLookingAlive()
    {
        var stale = DesktopVmStateBadges.For(true, HyperVVirtualMachineState.Running,
            DesktopVmStateBadges.SnapshotLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal("状态待确认（超过 20 秒未能刷新）", stale.Text);
        Assert.Equal(0xFF808080u, stale.Argb);
    }

    [Fact]
    public void AnUnknownMachineIsNotReportedAsAnything()
    {
        var badge = DesktopVmStateBadges.For(false, HyperVVirtualMachineState.Unknown, TimeSpan.Zero);
        Assert.Equal("状态未确认", badge.Text);
        Assert.Equal(0xFF808080u, badge.Argb);
    }
}
