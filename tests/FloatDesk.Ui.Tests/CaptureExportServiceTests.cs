using FloatDesk.App.Capture;
using FloatDesk.Core.Geometry;

namespace FloatDesk.Ui.Tests;

public sealed class CaptureExportServiceTests
{
    [Fact]
    public void CropPreservesPhysicalPixelsAcrossNegativeCoordinates()
    {
        var virtualBounds = new PixelRect(-200, 0, 600, 300);
        var sourcePixels = Enumerable.Range(0, 600 * 300 * 4)
            .Select(index => (byte)(index % 251))
            .ToArray();
        using var source = new FloatDesk.Core.Models.CaptureFrame(
            virtualBounds,
            600 * 4,
            sourcePixels);
        var target = new PixelRect(-100, 50, 400, 200);

        using var cropped = CaptureExportService.Crop(source, target);

        Assert.Equal(target, cropped.Bounds);
        Assert.Equal(400 * 4, cropped.Stride);
        Assert.Equal(400 * 200 * 4, cropped.Bgra32.Length);
        var expectedOffset = (50 * 600 + 100) * 4;
        Assert.Equal(sourcePixels[expectedOffset], cropped.Bgra32.Span[0]);
    }
}
