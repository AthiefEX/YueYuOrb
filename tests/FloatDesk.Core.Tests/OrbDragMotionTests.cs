using FloatDesk.Core.Geometry;

namespace FloatDesk.Core.Tests;

public sealed class OrbDragMotionTests
{
    [Fact]
    public void DragStartsAfterThresholdAndTracksLatestScreenMovement()
    {
        var motion = new OrbDragMotion();
        var window = new PixelRect(100, 200, 96, 96);
        motion.Begin(new PixelPoint(500, 600), window, thresholdPixels: 4);

        Assert.False(
            motion.TryUpdate(new PixelPoint(503, 602), out _));
        Assert.False(motion.IsDragging);

        Assert.True(
            motion.TryUpdate(new PixelPoint(515, 625), out var moved));
        Assert.True(motion.IsDragging);
        Assert.Equal(new PixelRect(115, 225, 96, 96), moved);

        Assert.True(
            motion.TryUpdate(new PixelPoint(530, 610), out var latest));
        Assert.Equal(new PixelRect(130, 210, 96, 96), latest);
    }

    [Fact]
    public void DragMovementUsesLatestPointerPositionWithoutAccumulatingDeltas()
    {
        var motion = new OrbDragMotion();
        motion.Begin(
            new PixelPoint(-20, 40),
            new PixelRect(-100, 50, 96, 96),
            thresholdPixels: 3);

        Assert.True(
            motion.TryUpdate(new PixelPoint(10, 55), out var first));
        Assert.True(
            motion.TryUpdate(new PixelPoint(38, 79), out var second));

        Assert.Equal(new PixelRect(-70, 65, 96, 96), first);
        Assert.Equal(new PixelRect(-42, 89, 96, 96), second);
    }

    [Fact]
    public void PositionLockedInteractionNeverProducesADragMove()
    {
        var motion = new OrbDragMotion();
        var window = new PixelRect(100, 200, 96, 96);
        motion.Begin(
            new PixelPoint(500, 600),
            window,
            thresholdPixels: 4,
            canDrag: false);

        Assert.False(
            motion.TryUpdate(new PixelPoint(540, 635), out var result));
        Assert.True(
            motion.IsDragging,
            "A locked drag must still be recognized so releasing away " +
            "from the orb does not open the panel as a click.");
        Assert.Equal(window, result);
    }
}
