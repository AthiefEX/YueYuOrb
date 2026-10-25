namespace FloatDesk.Core.Models;

public sealed record BrowserSite(Guid Id, string Name, Uri Url, int Order);
public enum NavigationDisposition { AllowHttps, ConfirmExternal, Block }
