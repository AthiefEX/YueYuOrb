using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App;
using FloatDesk.App.Launcher;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class LauncherIconCacheTests
{
    private static readonly byte[] OnePixelPng = CreateOnePixelPng();

    [Fact]
    public async Task ReusesCachedPngWithoutCallingExtractorAgain()
    {
        using var temp = new TemporaryDirectory();
        var calls = 0;
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) =>
            {
                calls++;
                return Task.FromResult<byte[]?>(OnePixelPng);
            });
        var item = NewLauncher(temp.CreateFile("app.exe"));

        var first = await cache.GetAsync(item, CancellationToken.None);
        var second = await cache.GetAsync(item, CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.IsFrozen);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CorruptCacheFallsBackToFreshExtraction()
    {
        using var temp = new TemporaryDirectory();
        var item = NewLauncher(temp.CreateFile("app.exe"));
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) => Task.FromResult<byte[]?>(OnePixelPng));
        var key = LauncherIconCache.CreateCacheKey(item);
        var cachedPath = Path.Combine(temp.Path, $"{key}.png");
        await File.WriteAllBytesAsync(cachedPath, [0x00]);

        var image = await cache.GetAsync(item, CancellationToken.None);

        Assert.NotNull(image);
        Assert.NotEmpty(await File.ReadAllBytesAsync(cachedPath));
    }

    [Fact]
    public async Task ExtractorFailureReturnsNullWithoutWritingPng()
    {
        using var temp = new TemporaryDirectory();
        var item = NewLauncher(temp.CreateFile("app.exe"));
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) => Task.FromResult<byte[]?>(null));

        var image = await cache.GetAsync(item, CancellationToken.None);

        Assert.Null(image);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.png"));
    }

    [Fact]
    public async Task PruneRemovesOrphansAndTemporaryFilesButKeepsReferencedCache()
    {
        using var temp = new TemporaryDirectory();
        var item = NewLauncher(temp.CreateFile("app.exe"));
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) => Task.FromResult<byte[]?>(OnePixelPng));
        await cache.GetAsync(item, CancellationToken.None);
        var referenced = Path.Combine(
            temp.Path,
            $"{LauncherIconCache.CreateCacheKey(item)}.png");
        var orphan = Path.Combine(temp.Path, "orphan.png");
        var temporary = Path.Combine(temp.Path, "orphan.tmp");
        await File.WriteAllBytesAsync(orphan, OnePixelPng);
        await File.WriteAllBytesAsync(temporary, OnePixelPng);

        cache.Prune([item]);

        Assert.True(File.Exists(referenced));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public void CacheKeyChangesWithPathAndFileWriteTimestamp()
    {
        using var temp = new TemporaryDirectory();
        var firstPath = temp.CreateFile("first.exe");
        var secondPath = temp.CreateFile("second.exe");
        var first = NewLauncher(firstPath);
        var second = NewLauncher(secondPath);
        var firstKey = LauncherIconCache.CreateCacheKey(first);
        var secondKey = LauncherIconCache.CreateCacheKey(second);

        File.SetLastWriteTimeUtc(
            firstPath,
            File.GetLastWriteTimeUtc(firstPath).AddMinutes(2));
        var changedKey = LauncherIconCache.CreateCacheKey(first);

        Assert.NotEqual(firstKey, secondKey);
        Assert.NotEqual(firstKey, changedKey);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneKeyInvokeExtractorOnce()
    {
        using var temp = new TemporaryDirectory();
        var item = NewLauncher(temp.CreateFile("app.exe"));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                return release.Task;
            });

        var first = cache.GetAsync(item, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = cache.GetAsync(item, CancellationToken.None);
        release.SetResult(OnePixelPng);

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FolderLauncherUsesDirectoryMetadataAndReturnsIcon()
    {
        using var temp = new TemporaryDirectory();
        var folder = Path.Combine(temp.Path, "工具");
        Directory.CreateDirectory(folder);
        var item = new LauncherItem(
            Guid.NewGuid(),
            "工具",
            LauncherKind.Folder,
            folder,
            [],
            null,
            0);
        var cache = new LauncherIconCache(
            temp.Path,
            (_, _) => Task.FromResult<byte[]?>(OnePixelPng));

        var key = LauncherIconCache.CreateCacheKey(item);
        var image = await cache.GetAsync(item, CancellationToken.None);

        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.NotNull(image);
    }

    [Fact]
    public void MissingLauncherPathStillProducesStableCacheKey()
    {
        var item = NewLauncher(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"));

        var first = LauncherIconCache.CreateCacheKey(item);
        var second = LauncherIconCache.CreateCacheKey(item);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AppDataPathsProvidesLauncherIconDirectory()
    {
        var paths = new AppDataPaths();

        Assert.Equal(
            Path.Combine(paths.Root, "launcher-icons"),
            paths.LauncherIcons);
    }

    private static LauncherItem NewLauncher(string path) =>
        new(
            Guid.NewGuid(),
            Path.GetFileNameWithoutExtension(path),
            LauncherKind.Exe,
            path,
            [],
            Path.GetDirectoryName(path),
            0);

    private static byte[] CreateOnePixelPng()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[] { 0x30, 0x80, 0xE0, 0xFF },
            4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FloatDeskLauncherIconCacheTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateFile(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, [0x4D, 0x5A]);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
