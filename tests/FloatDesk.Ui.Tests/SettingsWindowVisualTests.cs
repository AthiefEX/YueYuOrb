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
                     "实时翻译",
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
                     "SettingsPasswordBoxStyle",
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

        Assert.Equal(9, tabs.Length);
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

    [Fact]
    public void NavigationTabsUseTheCustomRoundedContainerStyle()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var tabControl = Assert.Single(
            document.Descendants()
                .Where(element => element.Name.LocalName == "TabControl"),
            element =>
                (string?)element.Attribute(
                    XName.Get(
                        "Name",
                        "http://schemas.microsoft.com/winfx/2006/xaml")) ==
                "SettingsTabControl");
        var tabs = document.Descendants()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToArray();

        Assert.Equal(
            "{StaticResource NavigationTabItemStyle}",
            (string?)tabControl.Attribute("ItemContainerStyle"));
        Assert.Equal(9, tabs.Length);
        Assert.All(
            tabs,
            tab => Assert.Equal(
                "{StaticResource NavigationTabItemStyle}",
                (string?)tab.Attribute("Style")));
    }

    [Fact]
    public void InputLockHelpExplainsKlidInterceptionAndRestore()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("英文非 IME", xaml, StringComparison.Ordinal);
        Assert.Contains("可加载", xaml, StringComparison.Ordinal);
        Assert.Contains("拦截切换快捷键", xaml, StringComparison.Ordinal);
        Assert.Contains("恢复原布局", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AboutPageIdentifiesAuthorRepositoryLicenseAndGplText()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);
        var code = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml.cs"));
        var license = File.ReadAllText(FindRepoFile("LICENSE")).TrimStart();

        Assert.Contains(
            "月语悬浮球 1.0.0",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "作者：七月风语",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "GPL-3.0-only",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"ProjectRepositoryLink\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnProjectRepositoryClick",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ProductInfo.RepositoryUrl",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "UseShellExecute = true",
            code,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "GNU GENERAL PUBLIC LICENSE",
            license,
            StringComparison.Ordinal);
        Assert.Contains(
            "Version 3, 29 June 2007",
            license,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AiPageExposesBrowserThemeSelector()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xaml = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains(
            "x:Name=\"BrowserThemeComboBox\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("Text=\"网页外观\"", xaml, StringComparison.Ordinal);
        foreach (var option in new[]
                 {
                     "跟随 月语悬浮球",
                     "浅色",
                     "深色"
                 })
        {
            Assert.Contains(option, xaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AppRuntimeSharesTranslationServicesAndDispatchesLifecycle()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/AppRuntime.cs"));

        foreach (var integration in new[]
                 {
                     "HotkeyAction.TranslateSelection",
                     "TranslateSelectionAsync",
                     "TranslateSelectionRequested",
                     "_translationRuntime.SetSessionLocked",
                     "_translationRuntime.SetEmergencyPaused",
                     "_translationRuntime.PauseForCapture",
                     "_translationRuntime.ResumeAfterCapture",
                     "new TranslationRuntime(",
                     "new SelectedTextReader()",
                     "new TranslationOverlayWindow()"
                 })
        {
            Assert.Contains(
                integration,
                source,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "_translationCredentials,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_translationProviders,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => _ocr.AvailableLanguages",
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
