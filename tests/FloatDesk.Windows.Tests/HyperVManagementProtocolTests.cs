using FloatDesk.Core.Models.DesktopAutomation;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVManagementProtocolTests
{
    [Fact]
    public void RejectsUnregisteredVmBeforeAnyOperation()
    {
        var request = new HyperVManagementRequest(
            Guid.NewGuid(), Guid.NewGuid(), HyperVManagementOperation.Start, null, new HashSet<Guid>());

        var error = Assert.Throws<InvalidOperationException>(() =>
            HyperVManagementProtocol.Validate(request));

        Assert.Equal("HyperVVmNotRegistered", error.Message);
    }

    [Fact]
    public void ResourceScriptContainsOnlyTypedValuesAndRequiresPoweredOffVm()
    {
        var vmId = Guid.NewGuid();
        var resources = new VmResourceProfile(
            Guid.NewGuid(), 4, 4096, 2048, 8192, 80, "D:\\VMs", "Default Switch");

        var script = HyperVManagementProtocol.BuildResourceScript(vmId, resources);

        Assert.Contains("Set-VM", script, StringComparison.Ordinal);
        Assert.Contains("VmMustBeOffForResourceChange", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Command", script, StringComparison.Ordinal);
    }
}
