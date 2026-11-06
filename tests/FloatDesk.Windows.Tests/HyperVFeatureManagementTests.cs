using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVFeatureManagementTests
{
    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(5, false, false)]
    public void PendingRestartDoesNotBecomeAnActiveBackend(int exitCode, bool success, bool restart)
    {
        var result = HyperVFeatureManagement.ClassifyExitCode(exitCode);
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(restart, result.RestartRequired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FeatureOperationIsScopedAndNeverRestartsWindows(bool enable)
    {
        var command = HyperVFeatureManagement.CreateCommand(enable);
        Assert.True(Path.IsPathFullyQualified(command.FileName));
        Assert.Equal("runas", command.Verb);
        Assert.Contains("/NoRestart", command.ArgumentList);
        Assert.Contains("/FeatureName:Microsoft-Hyper-V-All", command.ArgumentList);
        Assert.DoesNotContain(command.ArgumentList, argument => argument.Contains("Remove", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(command.ArgumentList, argument => argument.Contains("VirtualMachinePlatform", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManagementToolsCommandInstallsHyperVPowerShellOnly()
    {
        var command = HyperVFeatureManagement.CreateManagementToolsCommand();

        Assert.Contains("/Enable-Feature", command.ArgumentList);
        Assert.Contains("/FeatureName:Microsoft-Hyper-V-Management-PowerShell", command.ArgumentList);
        Assert.Contains("/NoRestart", command.ArgumentList);
        Assert.DoesNotContain(command.ArgumentList, argument =>
            argument.Contains("VirtualMachinePlatform", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ManagementToolsProbeUsesNativePowerShellAndGetVm()
    {
        var command = HyperVFeatureManagement.CreateManagementToolsQueryCommand();

        Assert.True(Path.IsPathFullyQualified(command.FileName));
        Assert.Contains("-NoProfile", command.ArgumentList);
        var script = string.Join(" ", command.ArgumentList);
        Assert.Contains("Get-Command Get-VM", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"available\":true}", true)]
    [InlineData("{\"available\":false}", false)]
    [InlineData("", false)]
    public void ParsesManagementToolsProbe(string output, bool expected)
    {
        Assert.Equal(expected,
            HyperVFeatureManagement.ParseManagementToolsAvailable(output));
    }

    [Fact]
    public void EnablingHypervisorConfiguresBootLaunch()
    {
        var command = HyperVFeatureManagement.CreateBootConfigurationCommand(true);

        Assert.Equal("bcdedit.exe", Path.GetFileName(command.FileName));
        Assert.Equal("runas", command.Verb);
        Assert.Contains("/set", command.ArgumentList);
        Assert.Contains("{current}", command.ArgumentList);
        Assert.Contains("hypervisorlaunchtype", command.ArgumentList);
        Assert.Contains("auto", command.ArgumentList);
    }

    [Fact]
    public void DisablingHypervisorTurnsOffBootLaunch()
    {
        var command = HyperVFeatureManagement.CreateBootConfigurationCommand(false);

        Assert.Contains("off", command.ArgumentList);
    }

    [Theory]
    [InlineData("State : Enabled", true)]
    [InlineData("State : Disabled", false)]
    [InlineData("状态 : 已启用", true)]
    [InlineData("State : Enable Pending", false)]
    public void ParsesDismFeatureState(string output, bool expectedEnabled)
    {
        Assert.Equal(
            expectedEnabled,
            HyperVFeatureManagement.ParseFeatureEnabled(output));
    }

    [Theory]
    [InlineData("hypervisorlaunchtype    Auto", true)]
    [InlineData("hypervisorlaunchtype    Off", false)]
    [InlineData("hypervisorlaunchtype    auto", true)]
    public void ParsesBootHypervisorState(string output, bool expectedEnabled)
    {
        Assert.Equal(
            expectedEnabled,
            HyperVFeatureManagement.ParseHypervisorLaunchEnabled(output));
    }

    [Theory]
    [InlineData("A hypervisor has been detected. Features required for Hyper-V will not be displayed.", true)]
    [InlineData("Hyper-V Requirements: VM Monitor Mode Extensions: Yes", false)]
    [InlineData("已检测到虚拟机监控程序。", true)]
    public void ParsesRunningHypervisorState(string output, bool expectedEnabled)
    {
        Assert.Equal(expectedEnabled, HyperVFeatureManagement.ParseHypervisorPresent(output));
    }

    [Fact]
    public void HyperVStatusSeparatesConfiguredBootFromRunningHypervisor()
    {
        var status = new HyperVFeatureStatus(
            FeatureEnabled: true,
            HypervisorLaunchEnabled: true,
            HypervisorRunning: false,
            ManagementToolsAvailable: false,
            RestartRequired: false,
            Detail: null);

        Assert.True(status.FeatureEnabled);
        Assert.True(status.HypervisorLaunchEnabled);
        Assert.False(status.HypervisorRunning);
    }
}
