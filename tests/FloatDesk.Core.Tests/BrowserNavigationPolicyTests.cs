using FloatDesk.Core.Models;
using FloatDesk.Core.Browser;

namespace FloatDesk.Core.Tests;

public sealed class BrowserNavigationPolicyTests
{
    [Theory]
    [InlineData("https://chat.deepseek.com/", NavigationDisposition.AllowHttps)]
    [InlineData("javascript:alert(1)", NavigationDisposition.Block)]
    [InlineData("file:///C:/Windows/win.ini", NavigationDisposition.Block)]
    [InlineData("data:text/html,hello", NavigationDisposition.Block)]
    public void UserNavigationRestrictsDangerousSchemes(
        string uri,
        NavigationDisposition expected)
    {
        Assert.Equal(expected, BrowserNavigationPolicy.Classify(uri, true));
    }

    [Fact]
    public void CustomProtocolRequiresExternalConfirmation()
    {
        Assert.Equal(
            NavigationDisposition.ConfirmExternal,
            BrowserNavigationPolicy.Classify("mailto:test@example.com", true));
    }
}
