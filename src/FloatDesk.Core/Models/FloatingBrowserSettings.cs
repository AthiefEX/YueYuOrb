namespace FloatDesk.Core.Models;

public sealed record BrowserWindowBounds(double Left, double Top, double Width, double Height);
public sealed record FloatingBrowserSettings
{
    public bool AlwaysOnTop { get; init; } = true;
    public bool OpenOnStartup { get; init; }
    public string StartupUrl { get; init; } = "";
    public double Opacity { get; init; } = 1;
    public BrowserWindowBounds? Bounds { get; init; }
}

public sealed record CustomBackgroundSettings
{
    public bool Enabled { get; init; }
    public double Opacity { get; init; } = 0.25;
}
