namespace FloatDesk.Windows.Tests;

public sealed class WindowCaptureExclusionTests
{
    [Fact]
    public void CaptureExclusionUsesExcludeFromCaptureAffinity()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs"));

        Assert.Contains(
            "WDA_EXCLUDEFROMCAPTURE",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetWindowDisplayAffinity",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}'.");
    }
}
