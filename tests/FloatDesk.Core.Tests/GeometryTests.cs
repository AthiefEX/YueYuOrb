using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class GeometryTests
{
    [Fact]
    public void PixelRectRejectsNegativeSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PixelRect(0, 0, -1, 20));
    }

    [Fact]
    public void PixelRectRejectsOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PixelRect(int.MaxValue, 0, 2, 20));
    }

    [Fact]
    public void SelectionFromReversePointsNormalizesWithoutLosingOnePixel()
    {
        var result = SelectionGeometry.FromPoints(
            new PixelPoint(300, 250),
            new PixelPoint(-100, 50));

        Assert.Equal(new PixelRect(-100, 50, 400, 200), result);
    }

    [Fact]
    public void OrbPlacementClampsBallInsideNegativeMonitor()
    {
        var result = OrbPlacement.Clamp(
            new PixelRect(-10, -520, 56, 56),
            new PixelRect(-1920, -100, 1920, 1080));

        Assert.Equal(new PixelRect(-56, -100, 56, 56), result);
    }

    [Fact]
    public void QuickPanelFallsBackToLeftWhenRightHasNoRoom()
    {
        var point = OrbPlacement.PlaceQuickPanel(
            orb: new PixelRect(1320, 500, 64, 64),
            desiredSize: new PixelSize(320, 440),
            workArea: new PixelRect(0, 0, 1400, 900));

        Assert.True(point.X < 1320);
        Assert.InRange(point.X, 0, 1400 - 320);
        Assert.InRange(point.Y, 0, 900 - 440);
    }

    [Fact]
    public void QuickPanelCentersTallerRadialMenuOnOrb()
    {
        var point = OrbPlacement.PlaceQuickPanel(
            orb: new PixelRect(1320, 500, 64, 64),
            desiredSize: new PixelSize(660, 560),
            workArea: new PixelRect(0, 0, 1400, 900));

        Assert.Equal(500 + (64 - 560) / 2, point.Y);
    }

    [Fact]
    public void QuickPanelKeepsOrbInsideWhenOpeningLeft()
    {
        var orb = new PixelRect(1320, 500, 64, 64);
        var point = OrbPlacement.PlaceQuickPanel(
            orb,
            desiredSize: new PixelSize(660, 560),
            workArea: new PixelRect(0, 0, 1400, 900));

        var orbCenterX = orb.Left + (orb.Width / 2.0);
        Assert.InRange(orbCenterX, point.X, point.X + 660);
    }

    [Fact]
    public void QuickPanelKeepsOrbInsideWhenOpeningRight()
    {
        var orb = new PixelRect(400, 500, 64, 64);
        var point = OrbPlacement.PlaceQuickPanel(
            orb,
            desiredSize: new PixelSize(660, 560),
            workArea: new PixelRect(0, 0, 1400, 900));

        Assert.Equal(orb.Left, point.X);
        Assert.InRange(
            orb.Left + (orb.Width / 2.0),
            point.X,
            point.X + 660);
    }
}
