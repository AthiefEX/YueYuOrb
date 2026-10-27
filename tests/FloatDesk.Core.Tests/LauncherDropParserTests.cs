using FloatDesk.Core.Launcher;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class LauncherDropParserTests
{
    [Fact]
    public void AddsExeAndShortcutAndKeepsSequentialOrder()
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.CreateFile("编辑器.EXE");
        var lnk = temp.CreateFile("浏览器.LNK");
        var resolver = new FakeResolver(path =>
        {
            var kind = Path.GetExtension(path).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase)
                ? LauncherKind.Exe
                : LauncherKind.Shortcut;
            return new LauncherImportResolution(
                Path.GetFileNameWithoutExtension(path),
                kind,
                path,
                kind == LauncherKind.Exe
                    ? Path.GetDirectoryName(path)
                    : null);
        });

        var result = LauncherDropParser.Parse(
            [exe, lnk],
            existing: [],
            resolver,
            createId: () => Guid.NewGuid());

        Assert.Equal(
            ["编辑器", "浏览器"],
            result.Added.Select(item => item.Name));
        Assert.Equal([0, 1], result.Added.Select(item => item.Order));
        Assert.Equal(
            Path.GetDirectoryName(exe),
            result.Added[0].WorkingDirectory);
        Assert.Null(result.Added[1].WorkingDirectory);
        Assert.All(result.Added, item => Assert.False(item.ShowInWheel));
    }

    [Fact]
    public void DeduplicatesNormalizedPathsAndBatchEntries()
    {
        using var temp = new TemporaryDirectory();
        var existingPath = temp.CreateFile("a.exe");
        var addedPath = temp.CreateFile("b.exe");
        var existing = new LauncherItem(
            Guid.NewGuid(),
            "A",
            LauncherKind.Exe,
            existingPath,
            [],
            null,
            0,
            ShowInWheel: true);
        var resolver = new FakeResolver(path =>
            new LauncherImportResolution(
                Path.GetFileNameWithoutExtension(path),
                LauncherKind.Exe,
                path,
                Path.GetDirectoryName(path)));

        var result = LauncherDropParser.Parse(
            [
                existingPath.ToUpperInvariant(),
                addedPath,
                addedPath.ToUpperInvariant()
            ],
            [existing],
            resolver,
            createId: () => Guid.NewGuid());

        Assert.Single(result.Added);
        Assert.Equal(2, result.Duplicates.Count);
    }

    [Fact]
    public void BrokenShortcutReturnsFailedResultWithoutAdding()
    {
        using var temp = new TemporaryDirectory();
        var shortcut = temp.CreateFile("broken.lnk");

        var result = LauncherDropParser.Parse(
            [shortcut],
            existing: [],
            new FakeResolver(_ => null));

        Assert.Empty(result.Added);
        Assert.Single(result.Failed);
    }

    [Fact]
    public void UnsupportedFileIsReportedWithoutCallingResolver()
    {
        using var temp = new TemporaryDirectory();
        var text = temp.CreateFile("notes.txt");
        var resolver = new FakeResolver(_ =>
            throw new Xunit.Sdk.XunitException(
                "Resolver must not run for unsupported files."));

        var result = LauncherDropParser.Parse(
            [text],
            existing: [],
            resolver);

        Assert.Empty(result.Added);
        Assert.Single(result.Unsupported);
    }

    [Fact]
    public void AppendsAfterHighestExistingOrder()
    {
        using var temp = new TemporaryDirectory();
        var path = temp.CreateFile("new.exe");
        var existing = new LauncherItem(
            Guid.NewGuid(),
            "Existing",
            LauncherKind.Exe,
            temp.CreateFile("existing.exe"),
            [],
            null,
            7);
        var resolver = new FakeResolver(candidate =>
            new LauncherImportResolution(
                Path.GetFileNameWithoutExtension(candidate),
                LauncherKind.Exe,
                candidate,
                Path.GetDirectoryName(candidate)));

        var result = LauncherDropParser.Parse(
            [path],
            [existing],
            resolver,
            createId: () => Guid.NewGuid());

        Assert.Equal(8, Assert.Single(result.Added).Order);
    }

    private sealed class FakeResolver(
        Func<string, LauncherImportResolution?> resolve) :
        ILauncherEntryResolver
    {
        public LauncherImportResolution? Resolve(string fullPath) =>
            resolve(fullPath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FloatDeskLauncherDropTests",
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
