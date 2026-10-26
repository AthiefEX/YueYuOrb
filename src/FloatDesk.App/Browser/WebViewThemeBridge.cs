using System.Runtime.InteropServices;
using FloatDesk.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace FloatDesk.App.Browser;

public static class WebViewThemeBridge
{
    public static bool IsOptionalFailure(Exception exception) =>
        exception is NotImplementedException or
            InvalidOperationException or
            COMException or
            ObjectDisposedException;

    public static void ApplyPreferredColorScheme(
        CoreWebView2 core,
        BrowserColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(core);
        core.Profile.PreferredColorScheme = scheme switch
        {
            BrowserColorScheme.Dark => CoreWebView2PreferredColorScheme.Dark,
            BrowserColorScheme.Light => CoreWebView2PreferredColorScheme.Light,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scheme),
                scheme,
                "Unknown browser color scheme.")
        };
    }

    public static async Task<string> ReplaceDocumentScriptAsync(
        CoreWebView2 core,
        string? existingScriptId,
        BrowserColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(core);
        if (existingScriptId is not null)
            core.RemoveScriptToExecuteOnDocumentCreated(existingScriptId);
        return await core.AddScriptToExecuteOnDocumentCreatedAsync(
            BrowserThemeScripts.BuildDocumentScript(scheme));
    }

    public static Task ApplyCurrentDocumentAsync(
        CoreWebView2 core,
        BrowserColorScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(core);
        return core.ExecuteScriptAsync(
            BrowserThemeScripts.BuildDocumentScript(scheme));
    }
}

internal sealed class CoreWebView2ThemeSurface : IBrowserThemeSurface
{
    private readonly CoreWebView2 _core;

    public CoreWebView2ThemeSurface(CoreWebView2 core)
    {
        ArgumentNullException.ThrowIfNull(core);
        _core = core;
    }

    public void ApplyPreferredColorScheme(BrowserColorScheme scheme) =>
        WebViewThemeBridge.ApplyPreferredColorScheme(_core, scheme);

    public Task<string> ReplaceDocumentScriptAsync(
        string? existingScriptId,
        BrowserColorScheme scheme) =>
        WebViewThemeBridge.ReplaceDocumentScriptAsync(
            _core,
            existingScriptId,
            scheme);

    public Task ApplyCurrentDocumentAsync(BrowserColorScheme scheme) =>
        WebViewThemeBridge.ApplyCurrentDocumentAsync(_core, scheme);
}
