using FloatDesk.Core.Geometry;

namespace FloatDesk.Core.Tests;

public sealed class TranslationOverlayPlacementPolicyTests
{
    [Fact]
    public void OverlayPlacementFindsNonOverlappingPositionOrReportsNoSpace()
    {
        var region = new PixelRect(80, 80, 700, 140);
        var placement = TranslationOverlayPlacementPolicy.PlaceOutside(
            region,
            new PixelSize(600, 80),
            new PixelRect(0, 0, 1920, 1080),
            new PixelPoint(100, 100));

        Assert.NotNull(placement);
        Assert.False(Intersects(placement.Value, region));

        Assert.Null(TranslationOverlayPlacementPolicy.PlaceOutside(
            new PixelRect(0, 0, 800, 600),
            new PixelSize(600, 200),
            new PixelRect(0, 0, 800, 600),
            new PixelPoint(0, 0)));
    }

    [Fact]
    public void PlacementUsesPreferredPositionBeforeAlternativeSides()
    {
        var placement = TranslationOverlayPlacementPolicy.PlaceOutside(
            new PixelRect(100, 100, 300, 100),
            new PixelSize(240, 80),
            new PixelRect(0, 0, 1000, 700),
            new PixelPoint(-40, 20));

        Assert.Equal(new PixelRect(0, 20, 240, 80), placement);
    }

    [Fact]
    public void PlacementFallsBackBelowThenAbove()
    {
        var region = new PixelRect(100, 400, 600, 100);
        var size = new PixelSize(400, 100);
        var workArea = new PixelRect(0, 0, 1000, 700);

        var below = TranslationOverlayPlacementPolicy.PlaceOutside(
            region,
            size,
            workArea,
            new PixelPoint(120, 420));

        Assert.Equal(new PixelRect(120, 508, 400, 100), below);

        var above = TranslationOverlayPlacementPolicy.PlaceOutside(
            region,
            size,
            workArea,
            new PixelPoint(120, 420),
            gap: 200);

        Assert.Equal(new PixelRect(120, 100, 400, 100), above);
    }

    [Fact]
    public void PlacementFallsBackRightThenLeft()
    {
        var size = new PixelSize(300, 80);
        var workArea = new PixelRect(0, 0, 1000, 600);

        var right = TranslationOverlayPlacementPolicy.PlaceOutside(
            new PixelRect(0, 0, 600, 600),
            size,
            workArea,
            new PixelPoint(20, 20));
        Assert.Equal(new PixelRect(608, 20, 300, 80), right);

        var left = TranslationOverlayPlacementPolicy.PlaceOutside(
            new PixelRect(400, 0, 600, 600),
            size,
            workArea,
            new PixelPoint(420, 20));
        Assert.Equal(new PixelRect(92, 20, 300, 80), left);
    }

    [Fact]
    public void PlacementRejectsOverlayLargerThanWorkArea()
    {
        Assert.Null(TranslationOverlayPlacementPolicy.PlaceOutside(
            new PixelRect(20, 20, 40, 40),
            new PixelSize(801, 80),
            new PixelRect(0, 0, 800, 600),
            new PixelPoint(0, 0)));
    }

    private static bool Intersects(PixelRect left, PixelRect right) =>
        left.Left < right.Right &&
        left.Right > right.Left &&
        left.Top < right.Bottom &&
        left.Bottom > right.Top;
}
