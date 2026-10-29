using System.Xml.Linq;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsWindowVisualTests
{
    [Fact]
    public void OrbAppearanceOffersCrystalModeWithoutChangingContourPreference()
    {
        var xaml = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var selector = xaml.Descendants().Single(e =>
            (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ==
            "OrbDisplayModeComboBox");

        Assert.Equal("{Binding OrbDisplayModeIndex}", (string?)selector.Attribute("SelectedIndex"));
        Assert.Equal(["经典", "水晶球"], selector.Elements().Select(e => (string?)e.Attribute("Content")));
        var shape = xaml.Descendants().Single(e =>
            (string?)e.Attribute("SelectedIndex") == "{Binding ImageShapeIndex}");
        Assert.Equal("{Binding IsClassicOrbMode}", (string?)shape.Attribute("IsEnabled"));
    }

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
                     "音频",
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

        Assert.Equal(10, tabs.Length);
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
        Assert.Equal(10, tabs.Length);
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
    public void WheelSettingsExposeAllThreeTranslationActionToggles()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");
        var wheelTab = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "TabItem" &&
                (string?)element.Attribute(xName) == "WheelSettingsTab");

        foreach (var (name, binding) in new[]
                 {
                     (
                         "ShowTranslateSelectionInWheelCheckBox",
                         "ShowTranslateSelectionInWheel"),
                     (
                         "ShowTranslateRegionInWheelCheckBox",
                         "ShowTranslateRegionInWheel"),
                     (
                         "ShowTranslateSubtitlesInWheelCheckBox",
                         "ShowTranslateSubtitlesInWheel")
                 })
        {
            var checkBox = wheelTab
                .Descendants()
                .Single(element =>
                    element.Name.LocalName == "CheckBox" &&
                    (string?)element.Attribute(xName) == name);
            Assert.Equal(
                $"{{Binding {binding}}}",
                (string?)checkBox.Attribute("IsChecked"));
            Assert.False(string.IsNullOrWhiteSpace(
                (string?)checkBox.Attribute(
                    "AutomationProperties.Name")));
        }
    }

    [Fact]
    public void WheelSettingsExposeLauncherDockVisibilityToggle()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");
        var checkBox = document
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "CheckBox" &&
                (string?)element.Attribute(xName) ==
                "ShowLauncherDockCheckBox");

        Assert.Equal(
            "{Binding ShowLauncherDock}",
            (string?)checkBox.Attribute("IsChecked"));
        Assert.Equal(
            "显示轮盘下方快捷应用栏",
            (string?)checkBox.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void QuickAppsPageExposesLauncherDropSurface()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");
        var surface = document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(xName) ==
                "LauncherDropSurface");

        Assert.Equal("True", (string?)surface.Attribute("AllowDrop"));
        Assert.Equal(
            "OnLauncherDragOver",
            (string?)surface.Attribute("DragOver"));
        Assert.Equal(
            "OnLauncherDragLeave",
            (string?)surface.Attribute("DragLeave"));
        Assert.Equal(
            "OnLauncherDrop",
            (string?)surface.Attribute("Drop"));
        Assert.Contains(
            "拖入 exe 或快捷方式",
            document.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAppsGridUsesLauncherIconPresenter()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");
        var launcherGrid = document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(xName) == "LauncherGrid");
        var iconColumn = launcherGrid
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "DataGridTemplateColumn");
        var icon = iconColumn
            .Descendants()
            .Single(element =>
                element.Name.LocalName == "LauncherIconView");
        var iconProviderBinding =
            (string?)icon.Attribute("IconProvider") ?? "";

        Assert.Equal("{Binding}", (string?)icon.Attribute("Launcher"));
        Assert.Contains(
            "DataContext.LauncherIconProvider",
            iconProviderBinding,
            StringComparison.Ordinal);
        Assert.Contains(
            "AncestorType=DataGrid",
            iconProviderBinding,
            StringComparison.Ordinal);
        Assert.Equal("26", (string?)icon.Attribute("IconSize"));
    }

    [Fact]
    public void SubtitleSettingsExposeAttachmentIntervalOpacityAndFontControls()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");

        foreach (var name in new[]
                 {
                     "SubtitleAttachmentComboBox",
                     "SubtitleRefreshIntervalTextBox",
                     "SubtitleOverlayOpacityTextBox",
                     "SubtitleFontSizeTextBox"
                 })
        {
            var element = document
                .Descendants()
                .Single(candidate =>
                    (string?)candidate.Attribute(xName) == name);
            Assert.False(string.IsNullOrWhiteSpace(
                (string?)element.Attribute("AutomationProperties.Name")));
        }

        var attachment = document
            .Descendants()
            .Single(element =>
                (string?)element.Attribute(xName) ==
                "SubtitleAttachmentComboBox");
        var attachmentOptions = attachment
            .Elements()
            .Where(element => element.Name.LocalName == "ComboBoxItem")
            .Select(element => (string?)element.Attribute("Content"))
            .ToArray();
        Assert.Collection(
            attachmentOptions,
            option => Assert.Equal("自动", option),
            option => Assert.Equal("字幕上方", option),
            option => Assert.Equal("字幕下方", option));
        Assert.Contains(
            "100–1500 ms",
            document.ToString(SaveOptions.DisableFormatting),
            StringComparison.Ordinal);
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
            "月语悬浮球 1.1.1",
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
                     "HotkeyAction.ToggleSubtitles",
                     "TranslateSelectionAsync",
                     "TranslateSelectionRequested",
                     "TranslateSubtitlesRequested",
                     "_translationOverlay.PauseResumeRequested",
                     "_translationRuntime.ReselectSubtitleRegionAsync",
                     "_translationRuntime.SetSessionLocked",
                     "_translationRuntime.SetEmergencyPaused",
                     "_translationRuntime.PauseForCapture",
                     "_translationRuntime.ResumeAfterCapture",
                     "new TranslationRuntime(",
                     "() => _foreground.Current,",
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

    [Fact]
    public void AppRuntimeSubtitleRoutesForceFreshRegionSelection()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/AppRuntime.cs"));
        var compact = string.Join(
            " ",
            source.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains(
            "case HotkeyAction.ToggleSubtitles: _ = ToggleSubtitlesAsync();",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "private void OnTranslateSubtitlesRequested() " +
            "{ _panel?.HideAnimated(); _ = ToggleSubtitlesAsync(); }",
            compact,
            StringComparison.Ordinal);

        var methodStart = compact.IndexOf(
            "private async Task ToggleSubtitlesAsync()",
            StringComparison.Ordinal);
        var methodEnd = compact.IndexOf(
            "private void OnReselectSubtitleRegionRequested()",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = compact[methodStart..methodEnd];
        Assert.Contains(
            "_translationRuntime.ToggleSubtitlesAsync( " +
            "_lifetime.Token, selectRegionOnStart: true);",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppRuntimeWiresSubtitleTrayCommands()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/AppRuntime.cs"));
        var compact = string.Join(
            " ",
            source.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));

        foreach (var integration in new[]
                 {
                     "_translationOverlay.PauseResumeRequested += " +
                     "OnToggleSubtitlePauseRequested;",
                     "_translationOverlay.ReselectRegionRequested += " +
                     "OnReselectSubtitleRegionRequested;",
                     "_translationOverlay.StopSubtitleRequested += " +
                     "OnStopSubtitlesRequested;",
                     "toggleSubtitlePause: OnToggleSubtitlePauseRequested",
                     "reselectSubtitleRegion: " +
                     "OnReselectSubtitleRegionRequested",
                     "stopSubtitles: OnStopSubtitlesRequested",
                     "_translationRuntime.SubtitleStateChanged += " +
                     "UpdateSubtitleTrayState;",
                     "_translationRuntime.SubtitleStateChanged -= " +
                     "UpdateSubtitleTrayState;",
                     "private void OnToggleSubtitlePauseRequested() " +
                     "{ _translationRuntime.ToggleSubtitlePause(); " +
                     "UpdateSubtitleTrayState(); }",
                     "UpdateSubtitleSession("
                 })
        {
            Assert.Contains(
                integration,
                compact,
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "_translationRuntime.IsSubtitleRunning",
            compact,
            StringComparison.Ordinal);
        Assert.Contains(
            "_translationRuntime.IsSubtitlePaused",
            compact,
            StringComparison.Ordinal);

        var toggleStart = compact.IndexOf(
            "private async Task ToggleSubtitlesAsync()",
            StringComparison.Ordinal);
        var toggleEnd = compact.IndexOf(
            "private void OnToggleSubtitlePauseRequested()",
            StringComparison.Ordinal);
        var toggleMethod = Between(toggleStart, toggleEnd);
        Assert.Contains(
            "UpdateSubtitleTrayState()",
            toggleMethod,
            StringComparison.Ordinal);

        var reselectStart = compact.IndexOf(
            "private async Task ReselectSubtitleRegionAsync()",
            StringComparison.Ordinal);
        var reselectEnd = compact.IndexOf(
            "private void OnStopSubtitlesRequested()",
            StringComparison.Ordinal);
        var reselectMethod = Between(reselectStart, reselectEnd);
        Assert.Contains(
            "UpdateSubtitleTrayState()",
            reselectMethod,
            StringComparison.Ordinal);

        var stopStart = compact.IndexOf(
            "private async Task StopSubtitlesAsync()",
            StringComparison.Ordinal);
        var stopEnd = compact.IndexOf(
            "private void UpdateSubtitleTrayState()",
            StringComparison.Ordinal);
        var stopMethod = Between(stopStart, stopEnd);
        Assert.Contains(
            "UpdateSubtitleTrayState()",
            stopMethod,
            StringComparison.Ordinal);

        string Between(int start, int end)
        {
            Assert.True(start >= 0 && end > start);
            return compact[start..end];
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
