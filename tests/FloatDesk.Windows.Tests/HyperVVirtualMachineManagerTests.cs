using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVVirtualMachineManagerTests
{
    [Fact]
    public void CreateScriptUsesSecondGenerationAndDoesNotAcceptArbitraryCommands()
    {
        var request = new HyperVVmCreateRequest(
            Guid.NewGuid(), "YueYu-Test", "D:\\YueYu\\VMs", "D:\\Install\\Windows.iso",
            4, 8192, 80, "Default Switch", true);

        var script = HyperVVirtualMachineManager.BuildCreateScript(request);

        Assert.Contains("New-VM", script, StringComparison.Ordinal);
        Assert.Contains("-Generation 2", script, StringComparison.Ordinal);
        Assert.Contains("Add-VMDvdDrive", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Command", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeleteRequiresProductOwnershipBeforeRemovingDisk()
    {
        var request = new HyperVVmDeleteRequest(Guid.NewGuid(), true, true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            HyperVVirtualMachineManager.ValidateDelete(request, ownedByProduct: false));

        Assert.Equal("HyperVVmOwnershipRequired", error.Message);
    }

    [Fact]
    public void ParsesVmListWithoutDependingOnLocalizedStateText()
    {
        var json = "[{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Test\",\"state\":\"Running\",\"generation\":2,\"memoryStartupMiB\":4096,\"processorCount\":4}]";
        var values = HyperVVirtualMachineManager.ParseVmList(json);
        Assert.Single(values);
        Assert.Equal("Test", values[0].Name);
        Assert.Equal(HyperVVirtualMachineState.Running, values[0].State);
    }
}
