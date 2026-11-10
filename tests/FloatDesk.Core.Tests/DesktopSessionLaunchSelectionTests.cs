using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class DesktopSessionLaunchSelectionTests
{
    [Fact]
    public void PickerOnlyContainsEnabledModes()
    {
        var local = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid() };
        var vm = local with { Id = Guid.NewGuid(), Backend = DesktopSessionBackendKind.HyperV };
        var settings = new DesktopSessionSettings(true, [local, vm], [])
        {
            LocalChild = new(true, null), HyperV = new(false, null)
        };
        Assert.Equal(local.Id, Assert.Single(DesktopSessionLaunchSelection.GetCandidates(settings)).Id);
        Assert.Equal(2, DesktopSessionLaunchSelection.GetCandidates(settings with { HyperV = new(true, null) }).Count);
        Assert.Empty(DesktopSessionLaunchSelection.GetCandidates(settings with { LocalChild = new(false, null) }));
    }
}
