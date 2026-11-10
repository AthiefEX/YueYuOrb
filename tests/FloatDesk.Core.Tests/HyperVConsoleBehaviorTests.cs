using System.Text.Json;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class HyperVConsoleBehaviorTests
{
    [Fact]
    public void SharedLocationsAreOptInAndPersistPerEnvironment()
    {
        var original = EnvironmentProfile.CreateDefault();
        Assert.False(original.HostSharingEnabled);
        Assert.Empty(original.HostSharedPaths);
        original = original with { HostSharingEnabled = true, HostSharedPaths = [@"F:\", @"D:\Games"] };
        var restored = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(original))!;
        Assert.True(restored.HostSharingEnabled);
        Assert.Equal(original.HostSharedPaths, restored.HostSharedPaths);
    }
    [Fact]
    public void OldEnvironmentDefaultsToLeavingVmRunning()
    {
        Assert.Equal(HyperVConsoleCloseAction.CloseView, EnvironmentProfile.CreateDefault().ConsoleCloseAction);
    }

    [Theory]
    [InlineData(HyperVConsoleCloseAction.CloseView)]
    [InlineData(HyperVConsoleCloseAction.SaveState)]
    [InlineData(HyperVConsoleCloseAction.Shutdown)]
    public void PersistsClosePolicyAlongWithResolution(HyperVConsoleCloseAction action)
    {
        var original = EnvironmentProfile.CreateDefault() with { ConsoleCloseAction = action, DesktopWidth = 1920, DesktopHeight = 1080 };
        var restored = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(original))!;
        Assert.Equal(action, restored.ConsoleCloseAction);
        Assert.Equal(1920, restored.DesktopWidth);
        Assert.Equal(1080, restored.DesktopHeight);
    }
}
