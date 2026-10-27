using System.Drawing;
using FloatDesk.Windows.Launcher;

namespace FloatDesk.Windows.Tests;

public sealed class LauncherIconExtractorTests
{
    [Fact]
    public async Task ExtractsPngFromSystemExecutable()
    {
        using var extractor = new LauncherIconExtractor();

        var png = await extractor.ExtractPngAsync(
            Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            CancellationToken.None);

        Assert.NotNull(png);
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            png.Take(4));
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        var hasTransparentPixel = false;
        for (var y = 0; y < bitmap.Height && !hasTransparentPixel; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A != 0)
                    continue;
                hasTransparentPixel = true;
                break;
            }
        }
        Assert.True(
            hasTransparentPixel,
            $"Shell icon PNG must keep a transparent background. " +
            $"Format={bitmap.PixelFormat}, size={bitmap.Width}x{bitmap.Height}, " +
            $"corner={bitmap.GetPixel(0, 0)}.");
    }
}
