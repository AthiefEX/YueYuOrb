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

    [Fact]
    public void ParsesSingleVmObjectReturnedByPowerShellJsonConversion()
    {
        var json = "{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"Single\",\"state\":\"Off\",\"generation\":2,\"memoryStartupMiB\":2048,\"processorCount\":2}";

        var values = HyperVVirtualMachineManager.ParseVmList(json);

        Assert.Single(values);
        Assert.Equal("Single", values[0].Name);
        Assert.Equal(HyperVVirtualMachineState.Off, values[0].State);
    }

    [Fact]
    public void ListScriptEmitsAnEmptyJsonArrayWhenNoVirtualMachinesExist()
    {
        var script = HyperVVirtualMachineManager.BuildListScript();

        Assert.Contains("$vms = @(Get-VM", script, StringComparison.Ordinal);
        Assert.Contains("'[]'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateScriptRollsBackPartialVmAndDiskWhenAConfigurationStepFails()
    {
        var request = new HyperVVmCreateRequest(
            Guid.NewGuid(), "YueYu-Test", "D:\\YueYu\\VMs", "D:\\Install\\Windows.iso",
            4, 8192, 80, "Default Switch", true);

        var script = HyperVVirtualMachineManager.BuildCreateScriptForTest(request);

        Assert.Contains("try", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catch", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-VM", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ListScriptMarksYueYuManagedVirtualMachines()
    {
        var script = HyperVVirtualMachineManager.BuildListScript();

        Assert.Contains("ownedByProduct", script, StringComparison.Ordinal);
        Assert.Contains("YueYuOrb:managed", script, StringComparison.Ordinal);
        Assert.Contains("月语分身-*", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesManagedVmOwnershipMarker()
    {
        var json = "[{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"orphan\",\"state\":\"Off\",\"generation\":2,\"memoryStartupMiB\":2048,\"processorCount\":2,\"ownedByProduct\":true}]";

        var values = HyperVVirtualMachineManager.ParseVmList(json);

        Assert.True(values[0].OwnedByProduct);
    }

    [Theory]
    [InlineData("2", HyperVVirtualMachineState.Running)]
    [InlineData("3", HyperVVirtualMachineState.Off)]
    [InlineData("6", HyperVVirtualMachineState.Saved)]
    [InlineData("9", HyperVVirtualMachineState.Paused)]
    public void ParsesNumericHyperVStateValues(string state, HyperVVirtualMachineState expected)
    {
        var json = "[{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"numeric\",\"state\":\"" + state + "\",\"generation\":2,\"memoryStartupMiB\":2048,\"processorCount\":2}]";

        var values = HyperVVirtualMachineManager.ParseVmList(json);

        Assert.Equal(expected, values[0].State);
    }

    [Fact]
    public void LifecycleScriptsDisableInteractivePowerShellConfirmation()
    {
        var start = PowerShellHyperVHost.BuildStartVmScript(Guid.NewGuid());
        var stop = PowerShellHyperVHost.BuildStopVmScript(Guid.NewGuid());
        var delete = HyperVVirtualMachineManager.BuildDeleteScript(Guid.NewGuid(), deleteDisk: true);

        Assert.Contains("-Confirm:$false", start, StringComparison.Ordinal);
        Assert.Contains("Stop-VM -VM $vm -Force -Confirm:$false", stop, StringComparison.Ordinal);
        Assert.Contains("-Confirm:$false", delete, StringComparison.Ordinal);
        Assert.Contains("'ok'", start, StringComparison.Ordinal);
        Assert.Contains("'ok'", stop, StringComparison.Ordinal);
        Assert.Contains("'ok'", delete, StringComparison.Ordinal);
    }

    [Fact]
    public void VmConnectCommandTargetsTheSelectedVmNameOnLocalHost()
    {
        const string vmName = "月语分身-20261107-1323";

        var command = HyperVVirtualMachineManager.BuildVmConnectCommand(vmName);

        Assert.Equal("vmconnect.exe", Path.GetFileName(command.FileName));
        Assert.Equal("localhost", command.ArgumentList[0]);
        Assert.Equal(vmName, command.ArgumentList[1]);
        Assert.True(command.UseShellExecute);
    }
}
