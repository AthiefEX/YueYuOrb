using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class StartupApplicationGuardTests
{
    [Fact]
    public void ChildSessionNeverStartsGuardedApplication()
    {
        Assert.False(StartupApplicationGuard.ShouldLaunch(2, 1, @"D:\Apps\clash.exe", []));
        Assert.False(StartupApplicationGuard.ShouldLaunch(1, uint.MaxValue, @"D:\Apps\clash.exe", []));
    }

    [Fact]
    public void ExistingConsoleInstanceIsNotLaunchedAgain()
    {
        Assert.False(StartupApplicationGuard.ShouldLaunch(1, 1, @"D:\Apps\clash.exe",
            [new StartupProcessIdentity(1, @"d:\apps\CLASH.exe")]));
        Assert.True(StartupApplicationGuard.ShouldLaunch(1, 1, @"D:\Apps\clash.exe", []));
        Assert.True(StartupApplicationGuard.ShouldLaunch(1, 1, @"D:\Apps\clash.exe",
            [new StartupProcessIdentity(1, @"D:\Other\clash.exe")]));
    }
}
