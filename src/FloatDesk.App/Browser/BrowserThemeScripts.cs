using FloatDesk.Core.Browser;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Browser;

public static class BrowserThemeScripts
{
    public static string BuildDocumentScript(BrowserColorScheme scheme)
    {
        var resolved = scheme switch
        {
            BrowserColorScheme.Dark => "dark",
            BrowserColorScheme.Light => "light",
            _ => throw new ArgumentOutOfRangeException(
                nameof(scheme),
                scheme,
                "Unknown browser color scheme.")
        };

        return $$"""
            (() => {
              if (window.top !== window ||
                  location.hostname !== '{{BrowserThemePolicy.DeepSeekHost}}') {
                return;
              }

              const scheme = '{{resolved}}';
              const applyTheme = () => {
                if (!document.documentElement) {
                  return;
                }
                if (scheme === 'dark') {
                  document.documentElement.dataset.dsDarkTheme = '';
                  document.documentElement.style.colorScheme = 'dark';
                } else {
                  document.documentElement.removeAttribute('data-ds-dark-theme');
                  document.documentElement.style.colorScheme = 'light';
                }
                window.__floatDeskThemeV1 = scheme;
              };

              applyTheme();
              requestAnimationFrame(applyTheme);
              setTimeout(applyTheme, 250);
            })();
            """;
    }
}
