using System.Text.Json;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class HyperVAutomationSettingsTests
{
    [Fact]
    public void LegacyConfigurationUsesConservativeDefaults()
    {
        var value = JsonSerializer.Deserialize<HyperVAutomationSettings>("{}")!;
        Assert.Equal(4, value.CpuCount);
        Assert.Equal(4096, value.MemoryStartupMiB);
        Assert.Equal(80, value.DiskSizeGiB);
        Assert.Equal(HyperVGpuMode.VirtualDisplay, value.GpuMode);
        Assert.False(value.UnattendedInstall);
        Assert.NotNull(value.Validate());
    }

    [Fact]
    public void CustomStorageIsRetainedAcrossSerialization()
    {
        var value = new HyperVAutomationSettings { StorageDirectory = @"D:\Virtual Machines" };
        var restored = JsonSerializer.Deserialize<HyperVAutomationSettings>(JsonSerializer.Serialize(value));
        Assert.Equal(value, restored);
        Assert.Null(restored!.Validate());
    }

    [Theory]
    [InlineData(1, 4096, 8192, 80)]
    [InlineData(129, 4096, 8192, 80)]
    [InlineData(4, 1024, 8192, 80)]
    [InlineData(4, 8192, 4096, 80)]
    [InlineData(4, 4096, 8192, 31)]
    public void InvalidResourcesAreRejected(int cpu, long startup, long maximum, long disk)
    {
        var value = new HyperVAutomationSettings
        {
            StorageDirectory = @"D:\VMs", CpuCount = cpu,
            MemoryStartupMiB = startup, MemoryMaximumMiB = maximum, DiskSizeGiB = disk
        };
        Assert.NotNull(value.Validate());
    }
}
