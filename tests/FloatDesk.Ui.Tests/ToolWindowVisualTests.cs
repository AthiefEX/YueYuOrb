using System.Xml.Linq;

namespace FloatDesk.Ui.Tests;

public sealed class ToolWindowVisualTests
{
    [Theory]
    [InlineData("AiBrowserWindow.xaml")]
    [InlineData("NotesWindow.xaml")]
    public void ContentWindowsDoNotReserveSpaceForLargeIntroductoryHeaders(string file)
    {
        var document = XDocument.Load(FindRepoFile("src/FloatDesk.App/Views/" + file));
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("FontSize") == "27");
        Assert.DoesNotContain(document.Descendants(), element => (string?)element.Attribute("Background") == "{DynamicResource SettingsHeroBrush}");
    }
    [Fact]
    public void AllWindowsUsePublicBrandAndAssemblyPackUris()
    {
        var viewsDirectory = Path.GetDirectoryName(FindRepoFile(
            "src/FloatDesk.App/Views/OrbWindow.xaml"));
        Assert.NotNull(viewsDirectory);
        var xamlFiles = Directory.GetFiles(
            viewsDirectory,
            "*.xaml",
            SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(xamlFiles);
        foreach (var path in xamlFiles)
        {
            var xaml = File.ReadAllText(path);
            Assert.DoesNotContain(
                "Title=\"FloatDesk",
                xaml,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Source=\"/FloatDesk;component/",
                xaml,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Text=\"FD\"",
                xaml,
                StringComparison.Ordinal);

            if (xaml.Contains(
                    "SettingsWindowResources.xaml",
                    StringComparison.Ordinal))
            {
                Assert.Contains(
                    "Source=\"/YueYuOrb;component/Resources/" +
                    "SettingsWindowResources.xaml\"",
                    xaml,
                    StringComparison.Ordinal);
            }
        }

        Assert.Contains(
            "Text=\"月\"",
            File.ReadAllText(FindRepoFile(
                "src/FloatDesk.App/Views/OrbWindow.xaml")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrayIconUsesCrescentAndOrbInsteadOfTextMark()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Bootstrap/TrayService.cs"));

        Assert.DoesNotContain(
            "DrawString(\"FD\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FillEllipse",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GraphicsPath",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SharedResourcesDefineRoundedToolWindowAndScrollBarStyles()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Resources/SettingsWindowResources.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        foreach (var key in new[]
                 {
                     "SettingsScrollBarStyle",
                     "SettingsScrollBarThumbStyle",
                     "ToolWindowShellStyle",
                     "ToolWindowTitleBarStyle",
                     "ToolWindowToolbarStyle",
                     "ToolbarButtonStyle",
                     "ToolWindowSectionStyle",
                     "ToolWindowEditorStyle",
                     "ToolWindowListBoxStyle"
                 })
        {
            Assert.Contains(
                $"x:Key=\"{key}\"",
                xaml,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            document.Descendants()
                .Where(element => element.Name.LocalName == "Style")
                .Where(element =>
                    (string?)element.Attribute("TargetType") ==
                    "{x:Type ScrollBar}"),
            style => (string?)style.Attribute("BasedOn") ==
                     "{StaticResource SettingsScrollBarStyle}");
    }

    [Theory]
    [InlineData(
        "src/FloatDesk.App/Views/NotesWindow.xaml",
        "NotesTitleBar",
        "SearchBox",
        "NotesList",
        "BodyBox")]
    [InlineData(
        "src/FloatDesk.App/Views/AiBrowserWindow.xaml",
        "AiTitleBar",
        "SiteComboBox",
        "BrowserHost",
        "StatusPanel")]
    public void ToolWindowsUseTheSharedBrandedChrome(
        string relativePath,
        string titleBarName,
        string primaryControlName,
        string contentName,
        string statusName)
    {
        var document = XDocument.Load(FindRepoFile(relativePath));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains(
            "SettingsWindowResources.xaml",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowChrome.WindowChrome",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource ToolWindowShellStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"x:Name=\"{titleBarName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"x:Name=\"{primaryControlName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"x:Name=\"{contentName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"x:Name=\"{statusName}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Content=\"后退\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Background=\"#F7F7F4\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AiToolbarPreservesNavigationPinAndZoomControls()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/AiBrowserWindow.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        foreach (var handler in new[]
                 {
                     "OnBackClick",
                     "OnForwardClick",
                     "OnRefreshClick",
                     "OnExternalClick",
                     "OnPinClick",
                     "OnZoomOutClick",
                     "OnZoomInClick"
                 })
        {
            Assert.Contains(
                $"Click=\"{handler}\"",
                xaml,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "Style=\"{StaticResource ToolbarButtonStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ZoomText\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "src/FloatDesk.App/Views/NotesWindow.xaml",
        "NotesMinimizeButton",
        "NotesMaximizeRestoreButton",
        "NotesCloseButton",
        "NotesMaximizeGlyph")]
    [InlineData(
        "src/FloatDesk.App/Views/AiBrowserWindow.xaml",
        "AiMinimizeButton",
        "AiMaximizeRestoreButton",
        "AiCloseButton",
        "AiMaximizeGlyph")]
    public void ToolWindowCaptionButtonsUseExplicitClickHandlers(
        string relativePath,
        string minimizeName,
        string maximizeName,
        string closeName,
        string maximizeGlyphName)
    {
        var document = XDocument.Load(FindRepoFile(relativePath));
        var xaml = document.ToString(SaveOptions.DisableFormatting);
        var buttons = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();

        foreach (var (name, handler) in new[]
                 {
                     (minimizeName, "OnMinimizeClick"),
                     (maximizeName, "OnMaximizeRestoreClick"),
                     (closeName, "OnCloseClick")
                 })
        {
            var button = Assert.Single(
                buttons,
                element =>
                    (string?)element.Attribute(
                        XName.Get(
                            "Name",
                            "http://schemas.microsoft.com/winfx/2006/xaml")) == name);
            Assert.Equal(
                handler,
                (string?)button.Attribute("Click"));
        }

        Assert.DoesNotContain(
            "SystemCommands.MinimizeWindowCommand",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SystemCommands.CloseWindowCommand",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            $"x:Name=\"{maximizeGlyphName}\"",
            xaml,
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
