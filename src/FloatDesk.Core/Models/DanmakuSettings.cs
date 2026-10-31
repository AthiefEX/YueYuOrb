namespace FloatDesk.Core.Models;

public sealed record DanmakuSettings
{
    public bool Enabled { get; init; }
    public bool ShowInWheel { get; init; }
    public bool QQ { get; init; } = true;
    public bool TIM { get; init; } = true;
    public bool WeChat { get; init; } = true;
    public bool Vertical { get; init; }
    public string TitleFilter { get; init; } = "";
    public string HighlightKeywords { get; init; } = "";
    public string Color { get; init; } = "#FFFFFF";
    public string HighlightColor { get; init; } = "#FFD166";
    public string FontFamily { get; init; } = "Microsoft YaHei UI";
    public double FontSize { get; init; } = 20;
    public double Opacity { get; init; } = .9;
    public double WidthDip { get; init; } = 600;
    public double HeightDip { get; init; } = 240;
    public string? MonitorId { get; init; }
    public double RelativeX { get; init; } = 1;
    public double RelativeY { get; init; }
    public double DurationSeconds { get; init; } = 8;
    public bool ShowTitle { get; init; } = true;
    public bool ReduceMotion { get; init; }

    public DanmakuSettings Normalize() => this with
    {
        TitleFilter = Limit(TitleFilter, 2048), HighlightKeywords = Limit(HighlightKeywords, 2048),
        Color = ValidColor(Color) ? Color : "#FFFFFF", HighlightColor = ValidColor(HighlightColor) ? HighlightColor : "#FFD166",
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Microsoft YaHei UI" : Limit(FontFamily, 100),
        FontSize = Clamp(FontSize, 8, 96, 20), Opacity = Clamp(Opacity, .1, 1, .9),
        WidthDip = Clamp(WidthDip, 120, 3840, 600), HeightDip = Clamp(HeightDip, 80, 2160, 240),
        RelativeX = Clamp(RelativeX, 0, 1, 1), RelativeY = Clamp(RelativeY, 0, 1, 0),
        DurationSeconds = Clamp(DurationSeconds, 3, 30, 8)
    };
    private static double Clamp(double x, double min, double max, double fallback) => double.IsFinite(x) ? Math.Clamp(x, min, max) : fallback;
    private static string Limit(string? text, int length) => (text ?? "")[..Math.Min(text?.Length ?? 0, length)];
    private static bool ValidColor(string? text) => text is { Length: 7 } && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);
}
