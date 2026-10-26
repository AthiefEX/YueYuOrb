using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using FloatDesk.App.Browser;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class BrowserThemeScriptTests
{
    [Fact]
    public void DarkScriptUsesDeepSeekOwnedThemeState()
    {
        var script = BrowserThemeScripts.BuildDocumentScript(
            BrowserColorScheme.Dark);

        Assert.Contains(
            "location.hostname !== 'chat.deepseek.com'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "window.top !== window",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "document.documentElement.dataset.dsDarkTheme = ''",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "document.documentElement.style.colorScheme = 'dark'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("__floatDeskThemeV1", script, StringComparison.Ordinal);
        Assert.Contains("'dark'", script, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", script, StringComparison.Ordinal);
        Assert.Contains(
            "setTimeout(applyTheme, 250)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("filter:", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LightScriptRemovesDeepSeekDarkState()
    {
        var script = BrowserThemeScripts.BuildDocumentScript(
            BrowserColorScheme.Light);

        Assert.Contains(
            "document.documentElement.removeAttribute('data-ds-dark-theme')",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "document.documentElement.style.colorScheme = 'light'",
            script,
            StringComparison.Ordinal);
        Assert.Contains("'light'", script, StringComparison.Ordinal);
    }

    [Fact]
    [SuppressMessage(
        "Design",
        "CA2201:Do not raise reserved exception types",
        Justification = "The test must exercise WebView2's reserved COM failure type.")]
    public void OptionalWebViewThemeFailuresAreIgnored()
    {
        var optionalFailures = new Exception[]
        {
            new NotImplementedException(),
            new InvalidOperationException(),
            new COMException(),
            new ObjectDisposedException("webview")
        };

        Assert.All(
            optionalFailures,
            exception => Assert.True(
                WebViewThemeBridge.IsOptionalFailure(exception)));
        Assert.False(WebViewThemeBridge.IsOptionalFailure(
            new OperationCanceledException()));
    }

    [Fact]
    public void RuntimeStateReplacesOnlyChangedOrResetScripts()
    {
        var state = new BrowserThemeRuntimeState();

        Assert.True(state.NeedsReplacement(BrowserColorScheme.Light));
        state.Record("script-light", BrowserColorScheme.Light);
        Assert.Equal("script-light", state.ScriptId);
        Assert.False(state.NeedsReplacement(BrowserColorScheme.Light));
        Assert.True(state.NeedsReplacement(BrowserColorScheme.Dark));

        state.Record("script-dark", BrowserColorScheme.Dark);
        Assert.False(state.NeedsReplacement(BrowserColorScheme.Dark));
        Assert.True(state.NeedsReplacement(BrowserColorScheme.Light));

        state.Reset();
        Assert.Null(state.ScriptId);
        Assert.True(state.NeedsReplacement(BrowserColorScheme.Dark));
    }
}
