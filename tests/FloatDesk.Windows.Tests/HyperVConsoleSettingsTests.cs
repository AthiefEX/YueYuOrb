using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVConsoleSettingsTests
{
    [Fact]
    public void DisplayConfigurationSavesRequestedResolutionWithoutOpeningEditDialog()
    {
        var script = HyperVConsoleSettings.BuildDisplayScript(Guid.NewGuid(), 1920, 1080);
        Assert.Contains("Drawing.Size(1920,1080)", script);
        Assert.DoesNotContain("/edit", script);
        Assert.Contains("$options.Save()", script);
        Assert.Contains("FullScreen=$false", script);
    }

    [Fact]
    public void SaveStateReleasesRunningResourcesRatherThanPausingVm()
    {
        var script = HyperVConsoleSettings.BuildCloseScript(Guid.NewGuid(), HyperVConsoleCloseAction.SaveState);
        Assert.Contains("Stop-VM -VM $vm -Save", script);
        Assert.DoesNotContain("Suspend-VM", script);
        Assert.DoesNotContain("TurnOff", script);
    }

    [Fact]
    public void CloseViewDoesNotChangeVmPower()
    {
        Assert.Equal("'ok'", HyperVConsoleSettings.BuildCloseScript(Guid.NewGuid(), HyperVConsoleCloseAction.CloseView));
    }
}
