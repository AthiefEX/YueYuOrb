using System.Xml.Linq;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsWindowVisualTests
{
    [Fact]
    public void SettingsWindowDeclaresAllNavigationAndRequiredControls()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        foreach (var header in new[]
                 {
                     "常规",
                     "悬浮球",
                     "快捷应用",
                     "轮盘",
                     "输入法",
                     "AI 网页",
                     "截图",
                     "关于"
                 })
        {
            Assert.Contains(header, xaml, StringComparison.Ordinal);
        }

        foreach (var name in new[]
                 {
                     "LauncherGrid",
                     "InputRuleGrid",
                     "BrowserSitesGrid",
                     "WheelSettingsTab",
                     "SaveStatusText"
                 })
        {
            Assert.Contains(
                $"x:Name=\"{name}\"",
                xaml,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "WindowChrome.WindowChrome",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectionChanged=\"OnSettingsTabSelectionChanged\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<TabItem Header=\"",
            document.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsResourcesExposeTheWindowDesignSystem()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Resources/SettingsWindowResources.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        foreach (var key in new[]
                 {
                     "SettingsCardStyle",
                     "ToggleSwitchStyle",
                     "NavigationTabItemStyle",
                     "SettingsTabControlStyle",
                     "ActionButtonStyle",
                     "PrimaryActionButtonStyle",
                     "SettingsTextBoxStyle",
                     "SettingsComboBoxStyle",
                     "SettingsDataGridStyle"
                 })
        {
            Assert.Contains(
                $"x:Key=\"{key}\"",
                xaml,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "SystemParameters.HighContrast",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NavigationTabsAndCaptionButtonsExposeAccessibleNames()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var tabs = document
            .Descendants()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToArray();

        Assert.Equal(8, tabs.Length);
        foreach (var tab in tabs)
        {
            var label = tab
                .Elements()
                .Single(element => element.Name.LocalName == "TabItem.Header")
                .Descendants()
                .Where(element => element.Name.LocalName == "TextBlock")
                .Select(element => (string?)element.Attribute("Text"))
                .Last(text => !string.IsNullOrWhiteSpace(text));
            Assert.Equal(
                label,
                (string?)tab.Attribute("AutomationProperties.Name"));
        }

        foreach (var name in new[]
                 {
                     "MinimizeSettingsButton",
                     "MaximizeSettingsButton",
                     "CloseSettingsButton"
                 })
        {
            var button = document
                .Descendants()
                .Single(element =>
                    element.Name.LocalName == "Button" &&
                    (string?)element.Attribute(
                        XName.Get(
                            "Name",
                            "http://schemas.microsoft.com/winfx/2006/xaml")) == name);
            Assert.False(string.IsNullOrWhiteSpace(
                (string?)button.Attribute("AutomationProperties.Name")));
        }
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
