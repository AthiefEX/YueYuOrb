namespace FloatDesk.Core.Models;

public enum DanmakuDisplayMode { Always, FullscreenOnly, WindowedOnly }
public sealed record NotificationApplication(string Id, string Name);

public sealed record DanmakuSettings
{
    public bool Enabled { get; init; }
    public bool ShowInWheel { get; init; }
    public bool ReceiveAllApplications { get; init; }
    public IReadOnlyList<NotificationApplication> SelectedApplications { get; init; } = [];
    public DanmakuDisplayMode DisplayMode { get; init; }
    public bool FollowFullscreenMonitor { get; init; } = true;
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
        SelectedApplications = NormalizeApplications(SelectedApplications),
        DisplayMode = Enum.IsDefined(DisplayMode) ? DisplayMode : DanmakuDisplayMode.Always,
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
    private static IReadOnlyList<NotificationApplication> NormalizeApplications(IReadOnlyList<NotificationApplication>? apps)
    {
        if (apps is null) return [];
        var result = apps.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Id) && a.Id.Length <= 512)
            .Select(a => new NotificationApplication(a.Id.Trim(), Limit(string.IsNullOrWhiteSpace(a.Name) ? a.Id : a.Name, 100)))
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
        return apps.SequenceEqual(result) ? apps : result;
    }
    public bool AllowsApplication(string id) => ReceiveAllApplications ||
        SelectedApplications.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
