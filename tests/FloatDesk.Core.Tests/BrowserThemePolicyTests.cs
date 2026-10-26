using FloatDesk.Core.Browser;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class BrowserThemePolicyTests
{
    [Theory]
    [InlineData(
        BrowserThemeMode.FollowApp,
        AppTheme.System,
        false,
        BrowserColorScheme.Light)]
    [InlineData(
        BrowserThemeMode.FollowApp,
        AppTheme.System,
        true,
        BrowserColorScheme.Dark)]
    [InlineData(
        BrowserThemeMode.FollowApp,
        AppTheme.Light,
        true,
        BrowserColorScheme.Light)]
    [InlineData(
        BrowserThemeMode.FollowApp,
        AppTheme.Dark,
        false,
        BrowserColorScheme.Dark)]
    [InlineData(
        BrowserThemeMode.Light,
        AppTheme.Dark,
        true,
        BrowserColorScheme.Light)]
    [InlineData(
        BrowserThemeMode.Dark,
        AppTheme.Light,
        false,
        BrowserColorScheme.Dark)]
    public void ResolveUsesRequestedAndAppTheme(
        BrowserThemeMode requested,
        AppTheme appTheme,
        bool systemDark,
        BrowserColorScheme expected)
    {
        Assert.Equal(
            expected,
            BrowserThemePolicy.Resolve(requested, appTheme, systemDark));
    }

    [Theory]
    [InlineData("https://chat.deepseek.com/", true)]
    [InlineData("https://CHAT.DEEPSEEK.COM/chat", true)]
    [InlineData("https://chat.deepseek.com:443/", true)]
    [InlineData("https://deepseek.com/", false)]
    [InlineData("https://chat.deepseek.com.evil.test/", false)]
    [InlineData("https://example.com/", false)]
    public void FindAdapterMatchesOnlyTheExactDeepSeekHost(
        string value,
        bool expectedMatch)
    {
        var adapter = BrowserThemePolicy.FindAdapter(new Uri(value));

        if (!expectedMatch)
        {
            Assert.Null(adapter);
            return;
        }

        Assert.NotNull(adapter);
        Assert.Equal("deepseek", adapter.Value.Id);
        Assert.Equal(BrowserThemePolicy.DeepSeekHost, adapter.Value.Host);
    }
}
