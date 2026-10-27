using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class ThemeResourceTests
{
    [Fact]
    public void PrimaryButtonHoverBackgroundIsBrush()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var resources = LoadApplicationResources();
                var style = Assert.IsType<Style>(
                    resources["PrimaryButtonStyle"]);
                var trigger = Assert.Single(
                    style.Triggers.OfType<Trigger>(),
                    candidate =>
                        candidate.Property == UIElement.IsMouseOverProperty &&
                        Equals(candidate.Value, true));
                var setter = Assert.Single(
                    trigger.Setters.OfType<Setter>(),
                    candidate =>
                        candidate.Property == Control.BackgroundProperty);

                if (setter.Value is DynamicResourceExtension dynamicResource)
                {
                    Assert.Equal("AccentDarkBrush", dynamicResource.ResourceKey);
                }
                else
                {
                    Assert.IsAssignableFrom<Brush>(setter.Value);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA test thread timed out.");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void DarkAndLightThemesReplaceEverySemanticBrush()
    {
        var resources = LoadApplicationResources();
        var keys = new[]
        {
            "AccentBrush",
            "AccentDarkBrush",
            "SurfaceBrush",
            "SurfaceRaisedBrush",
            "InkBrush",
            "MutedInkBrush",
            "SettingsCardBrush",
            "SettingsCardSubtleBrush",
            "SettingsFieldBrush",
            "SettingsLineBrush",
            "SettingsHoverBrush",
            "SettingsPressedBrush",
            "MenuLabelBrush",
            "MenuStrongBrush",
            "MenuMutedBrush",
            "MenuDockGlassBrush",
            "MenuActionBeamBrush"
        };

        ThemeService.ApplyToResources(
            resources,
            AppTheme.Dark,
            highContrast: false,
            systemUsesDarkTheme: false);
        var dark = keys.ToDictionary(
            key => key,
            key => GetColor(resources, key));

        ThemeService.ApplyToResources(
            resources,
            AppTheme.Light,
            highContrast: false,
            systemUsesDarkTheme: true);
        var light = keys.ToDictionary(
            key => key,
            key => GetColor(resources, key));

        foreach (var key in keys)
        {
            Assert.NotEqual(light[key], dark[key]);
        }
        Assert.True(Brightness(dark["SurfaceBrush"]) < Brightness(light["SurfaceBrush"]));
        Assert.True(Brightness(dark["InkBrush"]) > Brightness(light["InkBrush"]));
    }

    [Fact]
    public void RuntimeObservesWindowsThemePreferenceChanges()
    {
        var source = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/AppRuntime.cs"));

        Assert.Contains(
            "UserPreferenceChanged +=",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "UserPreferenceChanged -=",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationCaptureLifecycleIsWiredAcrossCoordinatorAndDisplayChanges()
    {
        var runtime = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/AppRuntime.cs"));
        var coordinator = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Coordination/WindowCoordinator.cs"));
        var subtitle = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Translation/SubtitleTranslationController.cs"));
        var overlay = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs"));

        Assert.Contains(
            "RegisterCaptureHiddenWindow(_translationOverlay)",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "HandleDisplayTopologyChanged()",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "PauseForCapture()",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResumeAfterCapture()",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "_captureHiddenWindows",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrepareSubtitleCaptureRegion",
            subtitle,
            StringComparison.Ordinal);
        Assert.Contains(
            "TranslationOverlayPlacementPolicy.PlaceOutside",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "_subtitleCaptureRegion",
            overlay,
            StringComparison.Ordinal);
        Assert.Contains(
            "Intersects(",
            overlay,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BrowserThemeChangesAlwaysReachOpenPopups()
    {
        var source = File.ReadAllText(FindRepoFile(
                "src/FloatDesk.App/Views/AiBrowserWindow.xaml.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var applySettings = SliceBetween(
            source,
            "public void ApplySettings",
            "public async Task ShowAndEnsureAsync");
        var ensureBrowser = SliceBetween(
            source,
            "private async Task EnsureBrowserAsync",
            "private async void OnSiteSelectionChanged");

        Assert.DoesNotContain(
            "_browser.ZoomFactor = _zoomPercent / 100.0;\n" +
            "            _ = ApplyThemeAsync();",
            applySettings,
            StringComparison.Ordinal);
        Assert.Contains(
            "ZoomText.Text = $\"{_zoomPercent}%\";\n" +
            "        _ = ApplyThemeAsync();",
            applySettings,
            StringComparison.Ordinal);
        Assert.Contains(
            "await ApplyThemeAsync();",
            ensureBrowser,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationOverlayFollowsDynamicThemeBrushes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var light = Color.FromRgb(242, 248, 255);
                var dark = Color.FromRgb(24, 37, 52);
                var window = new TranslationOverlayWindow();
                try
                {
                    window.Resources["SettingsCardBrush"] =
                        new SolidColorBrush(light);
                    var operation = window.BeginOperation();
                    window.ShowStatus(
                        operation,
                        "主题测试",
                        new FloatDesk.Core.Geometry.PixelPoint(20, 20));
                    window.UpdateLayout();

                    var card = Assert.IsType<Border>(
                        window.FindName("OverlayCard"));
                    Assert.Equal(
                        light,
                        Assert.IsType<SolidColorBrush>(
                            card.Background).Color);

                    window.Resources["SettingsCardBrush"] =
                        new SolidColorBrush(dark);
                    window.UpdateLayout();
                    Assert.Equal(
                        dark,
                        Assert.IsType<SolidColorBrush>(
                            card.Background).Color);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(8)),
            "STA test thread timed out.");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task WheelLauncherLabelsFollowThemeResources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var lightBackground = Color.FromRgb(250, 251, 252);
                var lightForeground = Color.FromRgb(20, 30, 45);
                var darkBackground = Color.FromRgb(32, 40, 52);
                var darkForeground = Color.FromRgb(240, 246, 255);
                var window = new FloatDesk.App.Views.QuickPanelWindow();
                window.Resources["MenuLabelBrush"] =
                    new SolidColorBrush(lightBackground);
                window.Resources["MenuStrongBrush"] =
                    new SolidColorBrush(lightForeground);

                var settings = AppSettings.CreateDefault() with
                {
                    Launchers =
                    [
                        new LauncherItem(
                            Guid.NewGuid(),
                            "主题测试应用",
                            LauncherKind.Exe,
                            @"C:\Apps\theme.exe",
                            [],
                            null,
                            0,
                            ShowInWheel: true)
                    ]
                };
                window.Apply(
                    settings,
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                AssertWheelLabelColors(
                    window,
                    lightBackground,
                    lightForeground);

                window.Resources["MenuLabelBrush"] =
                    new SolidColorBrush(darkBackground);
                window.Resources["MenuStrongBrush"] =
                    new SolidColorBrush(darkForeground);
                window.Apply(
                    settings,
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                AssertWheelLabelColors(
                    window,
                    darkBackground,
                    darkForeground);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        FileInfo? appXaml = null;
        while (directory is not null)
        {
            var candidate = new FileInfo(Path.Combine(
                directory.FullName,
                "src",
                "FloatDesk.App",
                "App.xaml"));
            if (candidate.Exists)
            {
                appXaml = candidate;
                break;
            }
            directory = directory.Parent;
        }
        Assert.NotNull(appXaml);

        var document = XDocument.Load(appXaml.FullName);
        var resources = document.Root?.Elements().Single(
            element => element.Name.LocalName == "Application.Resources");
        Assert.NotNull(resources);
        var innerXaml = string.Concat(
            resources.Nodes().Select(
                node => node.ToString(SaveOptions.DisableFormatting)));
        var resourceDictionaryXaml =
            "<ResourceDictionary " +
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            innerXaml +
            "</ResourceDictionary>";
        return Assert.IsType<ResourceDictionary>(
            XamlReader.Parse(resourceDictionaryXaml));
    }

    private static Color GetColor(
        ResourceDictionary resources,
        string key) =>
        Assert.IsType<SolidColorBrush>(resources[key]).Color;

    private static void AssertWheelLabelColors(
        FloatDesk.App.Views.QuickPanelWindow window,
        Color expectedBackground,
        Color expectedForeground)
    {
        var canvas = Assert.IsType<Canvas>(window.FindName("WheelCanvas"));
        var button = Assert.Single(
            canvas.Children.OfType<Button>(),
            candidate =>
                candidate.Tag is LauncherItem
                {
                    Name: "主题测试应用"
                });
        var content = Assert.IsType<Grid>(button.Content);
        var label = Assert.Single(
            content.Children.OfType<Border>(),
            candidate =>
                Grid.GetColumn(candidate) == 1 &&
                candidate.Child is TextBlock);
        var text = Assert.IsType<TextBlock>(label.Child);

        Assert.Equal(
            expectedBackground,
            Assert.IsType<SolidColorBrush>(label.Background).Color);
        Assert.Equal(
            expectedForeground,
            Assert.IsType<SolidColorBrush>(text.Foreground).Color);
    }

    private static double Brightness(Color color) =>
        ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) /
        byte.MaxValue;

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

    private static string SliceBetween(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker '{startMarker}'.");
        var end = source.IndexOf(
            endMarker,
            start + startMarker.Length,
            StringComparison.Ordinal);
        Assert.True(end > start, $"Missing source marker '{endMarker}'.");
        return source[start..end];
    }
}
