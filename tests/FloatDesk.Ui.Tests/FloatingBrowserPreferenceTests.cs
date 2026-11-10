using FloatDesk.App.ViewModels;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class FloatingBrowserPreferenceTests
{
    [Fact]
    public void SettingsApplyPreservesLiveBoundsBookmarksAndOpacity()
    {
        var baseline = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(baseline);
        vm.BrowserOpenOnStartup = true;
        vm.CustomBackgroundOpacityPercent = 67;
        var bounds = new BrowserWindowBounds(50, 60, 900, 720);
        var latest = baseline with { FloatingBrowser = baseline.FloatingBrowser with { Bounds = bounds, Opacity = .45 },
            BrowserSites = [.. baseline.BrowserSites, new(Guid.NewGuid(), "bookmark", new Uri("https://example.test/"), 50)] };
        var saved = vm.BuildCandidate(latest);
        Assert.Equal(bounds, saved.FloatingBrowser.Bounds);
        Assert.Equal(.45, saved.FloatingBrowser.Opacity);
        Assert.True(saved.FloatingBrowser.OpenOnStartup);
        Assert.Equal(.67, saved.CustomBackground.Opacity);
        Assert.Contains(saved.BrowserSites, site => site.Name == "bookmark");
    }

    [Fact]
    public void WatchingDoesNotActivateAndInteractionKeepsUnrelatedStyles()
    {
        const int unrelated = 0x00080080;
        var watching = AiBrowserWindow.InteractionStyle(unrelated, true);
        Assert.Equal(unrelated, watching & unrelated);
        Assert.NotEqual(0, watching & 0x20);
        Assert.NotEqual(0, watching & 0x08000000);
        Assert.Equal(unrelated, AiBrowserWindow.InteractionStyle(watching, false));
        Assert.False(AppSettings.CreateDefault().FloatingBrowser.OpenOnStartup);
    }
}
