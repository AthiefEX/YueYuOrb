using FloatDesk.Windows.Diagnostics;

namespace FloatDesk.Windows.Tests;

public sealed class ResourceUsageMathTests
{
    [Fact]
    public void CpuUsesKernelIncludingIdleAndUserDeltas()
    {
        var delta = new CpuUsageDelta();
        Assert.Null(delta.Update(new(100, 200, 100)));
        Assert.Equal(75d, delta.Update(new(150, 350, 150)));
    }

    [Fact]
    public void CpuRebasesAfterFailureResetOrZeroInterval()
    {
        var delta = new CpuUsageDelta();
        Assert.Null(delta.Update(new(10, 20, 10)));
        Assert.Null(delta.Update(new(10, 20, 10)));
        Assert.Null(delta.Update(new(1, 2, 1)));
        Assert.Null(delta.Update(null));
        Assert.Null(delta.Update(new(20, 40, 20)));
        Assert.Equal(50d, delta.Update(new(30, 50, 30)));
    }

    [Theory]
    [InlineData(0UL, 100d)]
    [InlineData(20UL, 0d)]
    public void CpuDistinguishesFullyBusyAndFullyIdle(ulong idleDelta, double expected)
    {
        var delta = new CpuUsageDelta();
        var baseline = ulong.MaxValue - 100;
        delta.Update(new(baseline, baseline, baseline));
        Assert.Equal(expected, delta.Update(new(baseline + idleDelta, baseline + 10, baseline + 10)));
    }

    [Fact]
    public void CpuRejectsIdleDeltaExceedingElapsedTime()
    {
        var delta = new CpuUsageDelta();
        delta.Update(new(0, 0, 0));
        Assert.Null(delta.Update(new(100, 10, 10)));
    }

    [Fact]
    public void GpuNormalizesHexIdentityAndSeparatesEqualEngineTypes()
    {
        var result = ResourceUsageMath.AggregateGpu([
            new("pid_1_luid_0xABCDEF01_0xABCDEF02_phys_0_eng_0_engtype_3D", 30),
            new("pid_2_luid_0xabcdef01_0xabcdef02_phys_0_eng_0_engtype_3D", 20),
            new("pid_1_luid_0xabcdef01_0xabcdef02_phys_0_eng_1_engtype_3D", 40)]);
        Assert.Equal(50d, result["luid_0xabcdef01_0xabcdef02"]);
    }

    [Fact]
    public void GpuSumsProcessesPerPhysicalEngineThenTakesBusiestEngine()
    {
        var result = ResourceUsageMath.AggregateGpu([
            new(Instance(1, 0, 0), 35), new(Instance(2, 0, 0), 45),
            new(Instance(1, 0, 1), 50), new(Instance(2, 1, 0), 60),
            new(Instance(1, 0, 0, "00000002"), 12)]);
        Assert.Equal(80d, result["luid_0x00000000_0x00000001"]);
        Assert.Equal(12d, result["luid_0x00000000_0x00000002"]);
    }

    [Fact]
    public void GpuClampsSumAndPreservesMeasuredZero()
    {
        var result = ResourceUsageMath.AggregateGpu([
            new(Instance(1, 0, 0), 80), new(Instance(2, 0, 0), 60),
            new(Instance(1, 0, 0, "00000002"), 0)]);
        Assert.Equal(100d, result["luid_0x00000000_0x00000001"]);
        Assert.Equal(0d, result["luid_0x00000000_0x00000002"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1d)]
    public void InvalidOrWarmingCounterMakesAdapterUnavailable(double? value)
    {
        var result = ResourceUsageMath.AggregateGpu([
            new(Instance(1, 0, 0), 20), new(Instance(2, 0, 1), value)]);
        Assert.Null(result["luid_0x00000000_0x00000001"]);
    }

    [Fact]
    public void MissingCountersDoNotRetainPreviousValuesAndMalformedNamesAreIgnored()
    {
        Assert.Single(ResourceUsageMath.AggregateGpu([new(Instance(1, 0, 0), 40)]));
        Assert.Empty(ResourceUsageMath.AggregateGpu([]));
        Assert.Empty(ResourceUsageMath.AggregateGpu([new("invalid", 99)]));
    }

    private static string Instance(int pid, int physical, int engine, string low = "00000001") =>
        $"pid_{pid}_luid_0x00000000_0x{low}_phys_{physical}_eng_{engine}_engtype_3D";
}
