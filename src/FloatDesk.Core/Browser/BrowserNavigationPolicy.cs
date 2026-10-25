using FloatDesk.Core.Models;

namespace FloatDesk.Core.Browser;

public static class BrowserNavigationPolicy
{
    public static NavigationDisposition Classify(string value, bool userInitiated)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return userInitiated ? NavigationDisposition.Block : NavigationDisposition.AllowHttps;
        if (uri.Scheme == Uri.UriSchemeHttps) return NavigationDisposition.AllowHttps;
        if (uri.Scheme is "javascript" or "file" or "data" or "about")
            return NavigationDisposition.Block;
        return NavigationDisposition.ConfirmExternal;
    }
}
