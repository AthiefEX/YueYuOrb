using System.Xml.Linq;

namespace FloatDesk.Ui.Tests;

public sealed class ReleaseConfigurationTests
{
    [Fact]
    public void ApplicationAssemblyUsesPublicProductName()
    {
        var project = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/FloatDesk.App.csproj"));
        var assemblyName = project
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "AssemblyName")
            .Value;

        Assert.Equal("YueYuOrb", assemblyName);
    }

    [Fact]
    public void PublishScriptUsesStablePortableArtifactNames()
    {
        var script = File.ReadAllText(FindRepoFile(
            "scripts/publish.ps1"));

        Assert.Contains(
            "YueYuOrb-$Version-win-x64-portable.zip",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "YueYuOrb.exe",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "SHA256SUMS.txt",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPreservesUpgradeAppIdAndCompatibilityDataPath()
    {
        var installer = File.ReadAllText(FindRepoFile(
            "installer/FloatDesk.iss"));

        Assert.Contains(
            "AppId={{CB89B2C1-41C1-47A6-8B73-CF54C6D0D403}",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            @"{localappdata}\FloatDesk",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "YueYuOrb-{#AppVersion}-win-x64-setup",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrivilegesRequired=lowest",
            installer,
            StringComparison.Ordinal);
        Assert.Contains(
            "Name: \"chinesesimp\"",
            installer,
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
