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
