using FloatDesk.Core.Models;
using FloatDesk.Windows.Launcher;

namespace FloatDesk.Windows.Tests;

public sealed class LauncherEntryResolverTests
{
    private readonly LauncherEntryResolver _resolver = new();

    [Fact]
    public void ResolvesExeWithItsWorkingDirectory()
    {
        using var temp = new TemporaryDirectory();
        var exe = temp.CreateFile("编辑器.exe");

        var resolved = _resolver.Resolve(exe);

        Assert.NotNull(resolved);
        Assert.Equal("编辑器", resolved.Name);
        Assert.Equal(LauncherKind.Exe, resolved.Kind);
        Assert.Equal(exe, resolved.Path);
        Assert.Equal(temp.Path, resolved.WorkingDirectory);
    }

    [Fact]
    public void LauncherEntryResolverPreservesShortcutPath()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.CreateFile("target.exe");
        var shortcut = temp.CreateShortcut("浏览器.lnk", target);

        var resolved = _resolver.Resolve(shortcut);

        Assert.NotNull(resolved);
        Assert.Equal("浏览器", resolved.Name);
        Assert.Equal(LauncherKind.Shortcut, resolved.Kind);
        Assert.Equal(shortcut, resolved.Path);
        Assert.Null(resolved.WorkingDirectory);
    }

    [Fact]
    public void MissingShortcutTargetReturnsNull()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.CreateFile("removed.exe");
        var shortcut = temp.CreateShortcut("removed.lnk", target);
        File.Delete(target);

        Assert.Null(_resolver.Resolve(shortcut));
    }

    [Fact]
    public void ShortcutIconSourcesIncludeShortcutTargetAndCustomIcon()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.CreateFile("target.exe");
        var icon = temp.CreateFile("custom.ico");
        var shortcut = temp.CreateShortcut(
            "browser.lnk",
            target,
            $"{icon},3");

        var sources = LauncherEntryResolver.GetIconSources(shortcut);

        Assert.Contains(
            sources,
            source => string.Equals(
                source.Path,
                shortcut,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sources,
            source => string.Equals(
                source.Path,
                target,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            sources,
            source => string.Equals(
                          source.Path,
                          icon,
                          StringComparison.OrdinalIgnoreCase) &&
                      source.Index == 3);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "FloatDeskLauncherResolverTests",
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

        public string CreateShortcut(
            string name,
            string targetPath,
            string? iconLocation = null)
        {
            var path = System.IO.Path.Combine(Path, name);
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("WScript.Shell is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create WScript.Shell.");
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path;
            if (iconLocation is not null)
                shortcut.IconLocation = iconLocation;
            shortcut.Save();
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
