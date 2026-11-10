using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class FloatingBrowserSettingsTests
{
    [Theory]
    [InlineData(0.1)] [InlineData(1.1)] [InlineData(double.NaN)]
    public void InvalidBrowserOpacityIsRejected(double opacity) =>
        Assert.Contains(SettingsValidator.Validate(AppSettings.CreateDefault() with
        { FloatingBrowser = new() { Opacity = opacity } }), error => error.Contains("FloatingBrowser", StringComparison.Ordinal));

    [Fact]
    public void InvalidBackgroundOpacityAndStartupSchemeAreRejected()
    {
        var errors = SettingsValidator.Validate(AppSettings.CreateDefault() with
        { CustomBackground = new() { Opacity = -1 }, FloatingBrowser = new() { StartupUrl = "javascript:alert(1)" } });
        Assert.Contains(errors, error => error.Contains("CustomBackground", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("StartupUrl", StringComparison.Ordinal));
    }
}
