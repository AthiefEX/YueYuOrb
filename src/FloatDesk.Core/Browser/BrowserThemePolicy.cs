using FloatDesk.Core.Models;

namespace FloatDesk.Core.Browser;

public readonly record struct BrowserThemeAdapter(string Id, string Host);

public static class BrowserThemePolicy
{
    public const string DeepSeekHost = "chat.deepseek.com";
    private static readonly BrowserThemeAdapter DeepSeekAdapter =
        new("deepseek", DeepSeekHost);

    public static BrowserColorScheme Resolve(
        BrowserThemeMode requested,
        AppTheme appTheme,
        bool systemUsesDarkTheme) =>
        requested switch
        {
            BrowserThemeMode.Light => BrowserColorScheme.Light,
            BrowserThemeMode.Dark => BrowserColorScheme.Dark,
            BrowserThemeMode.FollowApp => appTheme switch
            {
                AppTheme.Light => BrowserColorScheme.Light,
                AppTheme.Dark => BrowserColorScheme.Dark,
                AppTheme.System when systemUsesDarkTheme =>
                    BrowserColorScheme.Dark,
                AppTheme.System => BrowserColorScheme.Light,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(appTheme),
                    appTheme,
                    "Unknown application theme.")
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(requested),
                requested,
                "Unknown browser theme mode.")
        };

    public static BrowserThemeAdapter? FindAdapter(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var host = uri.IdnHost;
        if (host.EndsWith('.'))
            host = host[..^1];
        return string.Equals(host, DeepSeekHost, StringComparison.OrdinalIgnoreCase)
            ? DeepSeekAdapter
            : null;
    }
}
