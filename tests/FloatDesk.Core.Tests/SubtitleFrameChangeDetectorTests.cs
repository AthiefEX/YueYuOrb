using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Core.Tests;

public sealed class SubtitleFrameChangeDetectorTests
{
    [Fact]
    public void IdenticalFramesDoNotTriggerMeaningfulChange()
    {
        using var first = SolidFrame(0x20);
        using var second = SolidFrame(0x20);
        var a = SubtitleFrameChangeDetector.Create(first, 16, 9);
        var b = SubtitleFrameChangeDetector.Create(second, 16, 9);

        Assert.False(SubtitleFrameChangeDetector.HasMeaningfulChange(a, b));
    }

    [Fact]
    public void LargeFrameChangeExceedsDefaultThreshold()
    {
        using var first = SolidFrame(0x10);
        using var second = SolidFrame(0xE0);
        var a = SubtitleFrameChangeDetector.Create(first, 16, 9);
        var b = SubtitleFrameChangeDetector.Create(second, 16, 9);

        Assert.True(SubtitleFrameChangeDetector.HasMeaningfulChange(a, b));
    }

    [Fact]
    public void FirstFrameIsAlwaysMeaningful()
    {
        using var frame = SolidFrame(0x20);
        var current = SubtitleFrameChangeDetector.Create(frame, 16, 9);

        Assert.True(SubtitleFrameChangeDetector.HasMeaningfulChange(null, current));
    }

    [Fact]
    public void DifferenceReportsMeanAbsoluteLumaInNormalizedRange()
    {
        using var dark = SolidFrame(0);
        using var bright = SolidFrame(255);
        var a = SubtitleFrameChangeDetector.Create(dark, 16, 9);
        var b = SubtitleFrameChangeDetector.Create(bright, 16, 9);

        Assert.Equal(1.0, SubtitleFrameChangeDetector.Difference(a, b), 6);
    }

    private static CaptureFrame SolidFrame(byte value) =>
        new(
            new PixelRect(0, 0, 4, 4),
            16,
            Enumerable.Repeat(value, 4 * 4 * 4).ToArray());
}
