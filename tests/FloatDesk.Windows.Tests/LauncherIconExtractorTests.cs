using FloatDesk.Windows.Launcher;

namespace FloatDesk.Windows.Tests;

public sealed class LauncherIconExtractorTests
{
    [Fact]
    public async Task ExtractsPngFromSystemExecutable()
    {
        using var extractor = new LauncherIconExtractor();

        var png = await extractor.ExtractPngAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            CancellationToken.None);

        Assert.NotNull(png);
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47 },
            png.Take(4));
    }
}
