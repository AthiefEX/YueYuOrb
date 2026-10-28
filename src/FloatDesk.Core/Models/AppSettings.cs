namespace FloatDesk.Core.Models;

public enum AppTheme { System, Light, Dark }
public enum OrbImageShape { Circle, Contour }
public enum OrbDisplayMode { Classic, Crystal }
public enum FullscreenPolicy { FollowAlwaysOnTop, AutoHide, KeepTopmost }
public enum CaptureMode { Region, ActiveWindow, CurrentMonitor }
public enum ImageFormat { Png, Jpeg }
public enum HotkeyAction { Panel, RegionCapture, WindowCapture, MonitorCapture, PauseInput, AiBrowser, Notes, TranslateSelection, TranslateRegion, ToggleSubtitles, ExtractRegionText }
public readonly record struct HotkeyChord(ShortcutModifiers Modifiers, uint VirtualKey);

public sealed record GeneralSettings(bool StartWithWindows, AppTheme Theme);
public sealed record OrbSettings(
    int SizeDip,
    double Opacity,
    bool ShowClock,
    bool ShowSeconds,
    bool Clock24Hour,
    bool SnapToEdge,
    bool PositionLocked,
    OrbImageShape ImageShape)
{
    public OrbDisplayMode DisplayMode { get; init; } = OrbDisplayMode.Classic;
}
public sealed record OverlaySettings(bool AlwaysOnTop, FullscreenPolicy FullscreenPolicy);
public sealed record InputLockSettings(bool AutoEnabled, string TargetKlid, IReadOnlyList<AppRule> Rules);
public sealed record CaptureSettings(
    int DelaySeconds,
    ImageFormat Format,
    bool AutoSave,
    string? SaveDirectory,
    bool OpenEditor,
    string AnnotationColor,
    int StrokeWidthPx,
    int FontSizePx);
public sealed record WindowPlacement(
    string Id,
    string? MonitorId,
    double RelativeX,
    double RelativeY,
    double WidthDip,
    double HeightDip);

public sealed record AppSettings(
    int SchemaVersion,
    GeneralSettings General,
    OrbSettings Orb,
    OverlaySettings Overlay,
    InputLockSettings InputLock,
    IReadOnlyList<LauncherItem> Launchers,
    IReadOnlyList<BrowserSite> BrowserSites,
    Guid? SelectedBrowserSiteId,
    bool BrowserReleaseOnClose,
    int BrowserZoomPercent,
    bool BrowserTopmost,
    CaptureSettings Capture,
    IReadOnlyDictionary<HotkeyAction, HotkeyChord> Hotkeys,
    IReadOnlyList<WindowPlacement> Windows)
{
    public const int CurrentSchemaVersion = 1;
    public static IReadOnlyList<WheelActionKind> DefaultWheelActions { get; } =
    [
        WheelActionKind.RegionCapture,
        WheelActionKind.WindowCapture,
        WheelActionKind.MonitorCapture,
        WheelActionKind.AiBrowser,
        WheelActionKind.Notes,
        WheelActionKind.InputLock,
        WheelActionKind.Settings,
        WheelActionKind.TranslateSelection,
        WheelActionKind.TranslateRegion,
        WheelActionKind.TranslateSubtitles,
        WheelActionKind.ExtractRegionText
    ];

    public IReadOnlyList<WheelActionKind> WheelActions { get; init; } =
        DefaultWheelActions;

    public bool ShowLauncherDock { get; init; } = true;

    public BrowserThemeMode BrowserTheme { get; init; } =
        BrowserThemeMode.FollowApp;

    public TranslationSettings Translation { get; init; } =
        TranslationSettings.CreateDefault();

    public static AppSettings CreateDefault(string? picturesDirectory = null)
    {
        var deepSeek = new BrowserSite(
            Guid.Parse("1915f025-4279-4fc8-b70b-7d8a31c993dd"),
            "DeepSeek",
            new Uri("https://chat.deepseek.com/"),
            0);
        return new AppSettings(
            CurrentSchemaVersion,
            new GeneralSettings(false, AppTheme.System),
            new OrbSettings(56, 0.9, false, false, true, true, false, OrbImageShape.Circle),
            new OverlaySettings(true, FullscreenPolicy.FollowAlwaysOnTop),
            new InputLockSettings(true, "00000409", []),
            [],
            [deepSeek],
            deepSeek.Id,
            false,
            100,
            false,
            new CaptureSettings(
                0,
                ImageFormat.Png,
                false,
                picturesDirectory is null
                    ? null
                    : Path.Combine(picturesDirectory, ProductInfo.EnglishName),
                true,
                "#FF3B30",
                3,
                20),
            new Dictionary<HotkeyAction, HotkeyChord>
            {
                [HotkeyAction.Panel] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x20),
                [HotkeyAction.RegionCapture] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x41),
                [HotkeyAction.PauseInput] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x7B),
                [HotkeyAction.TranslateSelection] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x54),
                [HotkeyAction.TranslateRegion] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4F),
                [HotkeyAction.ToggleSubtitles] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4C),
                [HotkeyAction.ExtractRegionText] = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x45)
            },
            []);
    }
}
