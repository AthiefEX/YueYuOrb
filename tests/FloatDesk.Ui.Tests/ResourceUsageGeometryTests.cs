using FloatDesk.App.Views;
using Xunit;

namespace FloatDesk.Ui.Tests;

public sealed class ResourceUsageGeometryTests
{
    [Theory]
    [InlineData(0)] [InlineData(10)] [InlineData(25)] [InlineData(50)] [InlineData(90)] [InlineData(100)]
    public void LiquidUsesAreaRatherThanHeight(double percent)
    {
        var level = ResourceUsageGeometry.LiquidLevel(percent);
        var d = level * 2 - 1;
        var area = (Math.Acos(d) - d * Math.Sqrt(Math.Max(0, 1 - d * d))) / Math.PI;
        Assert.Equal(percent / 100, area, 6);
    }

    [Theory]
    [InlineData(1, 360)] [InlineData(2, 172)] [InlineData(3, 112)]
    public void RingSegmentsDivideCircleWithSmallGaps(int count, double sweep) =>
        Assert.Equal(sweep, ResourceUsageGeometry.SegmentSweep(count));

    [Theory]
    [InlineData(89.99, false)] [InlineData(90, true)] [InlineData(100, true)]
    public void WarningStartsAtNinety(double percent, bool warning) =>
        Assert.Equal(warning, ResourceUsageGeometry.IsWarning(percent));
}
