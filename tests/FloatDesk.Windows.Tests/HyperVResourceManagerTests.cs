using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVResourceManagerTests
{
    [Theory]
    [InlineData("[]", 0)]
    [InlineData("{\"name\":\"Default Switch\",\"type\":\"Internal\"}", 1)]
    [InlineData("[{\"name\":\"LAN\",\"type\":\"External\"}]", 1)]
    public void ReadsSwitchesRegardlessOfPipelineCardinality(string json, int count)
    {
        Assert.Equal(count, HyperVResourceManager.ParseSwitches(json).Count);
    }

    [Theory]
    [InlineData(1, 4096)]
    [InlineData(129, 4096)]
    [InlineData(4, 1024)]
    public void RejectsInvalidResourcesBeforeExecutingPowerShell(int cpu, long memory)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HyperVResourceManager.BuildApplyScript(Guid.NewGuid(), cpu, memory));
    }

    [Fact]
    public void ApplyRequiresOfflineVmAndReadsBackActualValues()
    {
        var script = HyperVResourceManager.BuildApplyScript(Guid.NewGuid(), 6, 6144);
        Assert.Contains("VmMustBeOffForResourceChange", script);
        Assert.Contains("-ProcessorCount 6", script);
        Assert.Contains("-MemoryStartupBytes 6144MB", script);
        Assert.Contains("catch", script);
        Assert.Contains("$oldCpu", script);
        Assert.Contains("ConvertTo-Json", script);
    }

    [Fact]
    public void GpuResourceChangesUseStaticMemoryAndCompatibleRollback()
    {
        var script = HyperVResourceManager.BuildApplyScript(Guid.NewGuid(), 4, 8192);
        Assert.Contains("Get-VMGpuPartitionAdapter", script);
        Assert.Contains("$hasGpu", script);
        Assert.Contains("$memoryMode.StaticMemory = $true", script);
        Assert.Contains("@memoryMode", script);
        Assert.Contains("$currentMemory.DynamicMemoryEnabled", script);
        Assert.Contains("$oldMemory", script);
        Assert.Contains("ResourceRollbackFailed", script);
        Assert.Contains("fixedMemoryForGpu", script);
    }

    [Fact]
    public void NetworkChangeValidatesSwitchAndPreservesOriginalConnectionForRollback()
    {
        var script = HyperVResourceManager.BuildNetworkScript(Guid.NewGuid(), "LAN's network");
        Assert.Contains("LAN''s network", script);
        Assert.Contains("Get-VMSwitch", script);
        Assert.Contains("$oldSwitch", script);
        Assert.Contains("Connect-VMNetworkAdapter", script);
        Assert.Contains("AmbiguousVmNetworkAdapters", script);
    }
}
