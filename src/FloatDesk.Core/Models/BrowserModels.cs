namespace FloatDesk.Core.Models;

public sealed record BrowserSite(Guid Id, string Name, Uri Url, int Order);
public enum BrowserThemeMode { FollowApp, Light, Dark }
public enum BrowserColorScheme { Light, Dark }
public enum NavigationDisposition { AllowHttps, ConfirmExternal, Block }
