using FloatDesk.Windows.Diagnostics;
using Xunit.Abstractions;

namespace FloatDesk.Windows.Tests;

public sealed class ResourceUsageNativeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeSamplingWarmsUpAndCanDisableAndRestartGpu()
    {
        using var sampler = new ResourceUsageSampler();
        var first = sampler.Sample();
        Assert.Null(first.CpuPercent);
        Assert.All(first.Gpus, gpu => Assert.Null(gpu.Percent));
        Assert.True(first.TotalMemoryBytes > 0);
        Assert.InRange(first.UsedMemoryBytes, 0UL, first.TotalMemoryBytes);
        Assert.InRange(first.MemoryPercent!.Value, 0, 100);
        await Task.Delay(1100);
        var next = sampler.Sample();
        Assert.InRange(next.CpuPercent!.Value, 0, 100);
        Assert.Equal(next.Gpus.Count, next.Gpus.Select(g => g.Id).Distinct().Count());
        foreach (var gpu in next.Gpus)
        {
            output.WriteLine($"{gpu.Id} {gpu.Name}: {gpu.Percent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"}");
            if (gpu.Percent is { } percent) Assert.InRange(percent, 0, 100);
        }
        Assert.Empty(sampler.Sample(false).Gpus);
        Assert.All(sampler.Sample().Gpus, gpu => Assert.Null(gpu.Percent));
        sampler.Dispose();
        sampler.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sampler.Sample());
    }
}
