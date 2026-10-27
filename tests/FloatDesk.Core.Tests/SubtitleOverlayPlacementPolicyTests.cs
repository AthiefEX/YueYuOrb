using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class SubtitleOverlayPlacementPolicyTests
{
    [Fact]
    public void PlaceAnchoredReturnsExactAboveAndBelowRectangles()
    {
        var region = new PixelRect(100, 300, 600, 100);
        var size = new PixelSize(400, 80);
        var workArea = new PixelRect(0, 0, 1000, 800);

        Assert.Equal(
            new PixelRect(200, 212, 400, 80),
            SubtitleOverlayPlacementPolicy.PlaceAnchored(
                region,
                size,
                workArea,
                SubtitleAttachmentMode.Above));

        Assert.Equal(
            new PixelRect(200, 408, 400, 80),
            SubtitleOverlayPlacementPolicy.PlaceAnchored(
                region,
                size,
                workArea,
                SubtitleAttachmentMode.Below));
    }

    [Fact]
    public void PlaceAnchoredAutoChoosesSideWithMoreUsableRoom()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(100, 300, 600, 100),
            new PixelSize(400, 80),
            new PixelRect(0, 0, 1000, 800),
            SubtitleAttachmentMode.Auto);

        Assert.Equal(new PixelRect(200, 408, 400, 80), placement);
    }

    [Fact]
    public void PlaceAnchoredAutoChoosesAboveWhenRoomIsEqual()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(100, 160, 600, 80),
            new PixelSize(400, 60),
            new PixelRect(0, 0, 1000, 400),
            SubtitleAttachmentMode.Auto);

        Assert.Equal(new PixelRect(200, 92, 400, 60), placement);
    }

    [Fact]
    public void PlaceAnchoredSupportsNegativeMonitorCoordinates()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(-1500, 300, 600, 100),
            new PixelSize(400, 80),
            new PixelRect(-1920, 0, 1920, 1080),
            SubtitleAttachmentMode.Above);

        Assert.Equal(new PixelRect(-1400, 212, 400, 80), placement);
    }

    [Fact]
    public void PlaceAnchoredFallsBackToOppositeSideWhenRequestedSideDoesNotFit()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(100, 20, 600, 100),
            new PixelSize(400, 80),
            new PixelRect(0, 0, 1000, 400),
            SubtitleAttachmentMode.Above);

        Assert.Equal(new PixelRect(200, 128, 400, 80), placement);
    }

    [Fact]
    public void PlaceAnchoredReturnsNullWhenNeitherSideFits()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(0, 0, 1000, 600),
            new PixelSize(400, 80),
            new PixelRect(0, 0, 1000, 600),
            SubtitleAttachmentMode.Auto);

        Assert.Null(placement);
    }

    [Fact]
    public void PlaceAnchoredAllowsClampedCaptureOverlapWhenRequested()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(100, 80, 600, 500),
            new PixelSize(400, 80),
            new PixelRect(0, 0, 1000, 600),
            SubtitleAttachmentMode.Above,
            allowCaptureOverlap: true);

        Assert.Equal(new PixelRect(200, 0, 400, 80), placement);
    }

    [Fact]
    public void PlaceAnchoredRejectsOverlayLargerThanWorkArea()
    {
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            new PixelRect(20, 20, 40, 40),
            new PixelSize(801, 80),
            new PixelRect(0, 0, 800, 600),
            SubtitleAttachmentMode.Auto,
            allowCaptureOverlap: true);

        Assert.Null(placement);
    }
}
