using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private AppSettings _baseline = AppSettings.CreateDefault();

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _orbSizeDip;
    [ObservableProperty] private double _orbOpacityPercent;
    [ObservableProperty] private bool _showClock;
    [ObservableProperty] private bool _showSeconds;
    [ObservableProperty] private bool _clock24Hour;
    [ObservableProperty] private bool _snapToEdge;
    [ObservableProperty] private bool _positionLocked;
    [ObservableProperty] private int _imageShapeIndex;
    [ObservableProperty] private bool _showRegionCaptureInWheel = true;
    [ObservableProperty] private bool _showWindowCaptureInWheel = true;
    [ObservableProperty] private bool _showMonitorCaptureInWheel = true;
    [ObservableProperty] private bool _showAiBrowserInWheel = true;
    [ObservableProperty] private bool _showNotesInWheel = true;
    [ObservableProperty] private bool _showInputLockInWheel = true;
    [ObservableProperty] private bool _showSettingsInWheel = true;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private int _fullscreenPolicyIndex;
    [ObservableProperty] private bool _autoInputEnabled;
    [ObservableProperty] private string _targetKlid = "00000409";
    [ObservableProperty] private bool _browserReleaseOnClose;
    [ObservableProperty] private int _browserZoomPercent;
    [ObservableProperty] private bool _browserTopmost;
    [ObservableProperty] private int _captureDelayIndex;
    [ObservableProperty] private int _captureFormatIndex;
    [ObservableProperty] private bool _captureAutoSave;
    [ObservableProperty] private string _captureSaveDirectory = "";
    [ObservableProperty] private bool _captureOpenEditor;
    [ObservableProperty] private string _annotationColor = "#FF3B30";
    [ObservableProperty] private int _strokeWidthPx;
    [ObservableProperty] private int _fontSizePx;
    [ObservableProperty] private string _panelHotkey = "Ctrl+Alt+Space";
    [ObservableProperty] private string _regionCaptureHotkey = "Ctrl+Alt+A";
    [ObservableProperty] private string _pauseHotkey = "Ctrl+Alt+F12";

    public ObservableCollection<LauncherItem> Launchers { get; } = [];
    public ObservableCollection<AppRule> InputRules { get; } = [];
    public ObservableCollection<BrowserSite> BrowserSites { get; } = [];
    public IReadOnlyList<InputAction> InputActions { get; } =
        [InputAction.LockEnglish, InputAction.Exclude];

    public static SettingsViewModel From(AppSettings settings)
    {
        var wheelActions = settings.WheelActions.ToHashSet();
        var vm = new SettingsViewModel
        {
            StartWithWindows = settings.General.StartWithWindows,
            ThemeIndex = (int)settings.General.Theme,
            OrbSizeDip = settings.Orb.SizeDip,
            OrbOpacityPercent = Math.Round(settings.Orb.Opacity * 100),
            ShowClock = settings.Orb.ShowClock,
            ShowSeconds = settings.Orb.ShowSeconds,
            Clock24Hour = settings.Orb.Clock24Hour,
            SnapToEdge = settings.Orb.SnapToEdge,
            PositionLocked = settings.Orb.PositionLocked,
            ImageShapeIndex = (int)settings.Orb.ImageShape,
            ShowRegionCaptureInWheel =
                wheelActions.Contains(WheelActionKind.RegionCapture),
            ShowWindowCaptureInWheel =
                wheelActions.Contains(WheelActionKind.WindowCapture),
            ShowMonitorCaptureInWheel =
                wheelActions.Contains(WheelActionKind.MonitorCapture),
            ShowAiBrowserInWheel =
                wheelActions.Contains(WheelActionKind.AiBrowser),
            ShowNotesInWheel =
                wheelActions.Contains(WheelActionKind.Notes),
            ShowInputLockInWheel =
                wheelActions.Contains(WheelActionKind.InputLock),
            ShowSettingsInWheel =
                wheelActions.Contains(WheelActionKind.Settings),
            AlwaysOnTop = settings.Overlay.AlwaysOnTop,
            FullscreenPolicyIndex = (int)settings.Overlay.FullscreenPolicy,
            AutoInputEnabled = settings.InputLock.AutoEnabled,
            TargetKlid = settings.InputLock.TargetKlid,
            BrowserReleaseOnClose = settings.BrowserReleaseOnClose,
            BrowserZoomPercent = settings.BrowserZoomPercent,
            BrowserTopmost = settings.BrowserTopmost,
            CaptureDelayIndex = settings.Capture.DelaySeconds switch { 3 => 1, 5 => 2, _ => 0 },
            CaptureFormatIndex = settings.Capture.Format == ImageFormat.Jpeg ? 1 : 0,
            CaptureAutoSave = settings.Capture.AutoSave,
            CaptureSaveDirectory = settings.Capture.SaveDirectory ?? "",
            CaptureOpenEditor = settings.Capture.OpenEditor,
            AnnotationColor = settings.Capture.AnnotationColor,
            StrokeWidthPx = settings.Capture.StrokeWidthPx,
            FontSizePx = settings.Capture.FontSizePx
        };
        foreach (var item in settings.Launchers.OrderBy(item => item.Order))
            vm.Launchers.Add(item);
        foreach (var rule in settings.InputLock.Rules)
            vm.InputRules.Add(rule);
        foreach (var site in settings.BrowserSites.OrderBy(site => site.Order))
            vm.BrowserSites.Add(site);
        vm.PanelHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.Panel));
        vm.RegionCaptureHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.RegionCapture));
        vm.PauseHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.PauseInput));
        vm._baseline = settings;
        return vm;
    }

    public AppSettings BuildCandidate(AppSettings latest)
    {
        var general = latest.General;
        if (StartWithWindows != _baseline.General.StartWithWindows)
            general = general with { StartWithWindows = StartWithWindows };
        if ((AppTheme)ThemeIndex != _baseline.General.Theme)
            general = general with { Theme = (AppTheme)ThemeIndex };

        var orb = latest.Orb;
        if (OrbSizeDip != _baseline.Orb.SizeDip)
            orb = orb with { SizeDip = OrbSizeDip };
        if (Math.Abs(OrbOpacityPercent / 100.0 - _baseline.Orb.Opacity) > 0.0001)
            orb = orb with { Opacity = OrbOpacityPercent / 100.0 };
        if (ShowClock != _baseline.Orb.ShowClock)
            orb = orb with { ShowClock = ShowClock };
        if (ShowSeconds != _baseline.Orb.ShowSeconds)
            orb = orb with { ShowSeconds = ShowSeconds };
        if (Clock24Hour != _baseline.Orb.Clock24Hour)
            orb = orb with { Clock24Hour = Clock24Hour };
        if (SnapToEdge != _baseline.Orb.SnapToEdge)
            orb = orb with { SnapToEdge = SnapToEdge };
        if (PositionLocked != _baseline.Orb.PositionLocked)
            orb = orb with { PositionLocked = PositionLocked };
        if ((OrbImageShape)ImageShapeIndex != _baseline.Orb.ImageShape)
            orb = orb with { ImageShape = (OrbImageShape)ImageShapeIndex };

        var overlay = latest.Overlay;
        if (AlwaysOnTop != _baseline.Overlay.AlwaysOnTop)
            overlay = overlay with { AlwaysOnTop = AlwaysOnTop };
        if ((FullscreenPolicy)FullscreenPolicyIndex != _baseline.Overlay.FullscreenPolicy)
            overlay = overlay with { FullscreenPolicy = (FullscreenPolicy)FullscreenPolicyIndex };

        var inputLock = latest.InputLock;
        if (AutoInputEnabled != _baseline.InputLock.AutoEnabled)
            inputLock = inputLock with { AutoEnabled = AutoInputEnabled };
        var targetKlid = TargetKlid.Trim().ToUpperInvariant();
        if (!string.Equals(targetKlid, _baseline.InputLock.TargetKlid, StringComparison.Ordinal))
            inputLock = inputLock with { TargetKlid = targetKlid };
        var inputRules = InputRules.ToArray();
        if (!inputRules.SequenceEqual(_baseline.InputLock.Rules))
            inputLock = inputLock with { Rules = inputRules };

        var launchers = Launchers
            .Select((item, index) => item with { Order = index })
            .ToArray();
        if (LaunchersEqual(
                launchers,
                _baseline.Launchers))
        {
            launchers = latest.Launchers.ToArray();
        }

        var wheelActions = BuildWheelActions();
        var effectiveWheelActions = wheelActions
            .ToHashSet()
            .SetEquals(_baseline.WheelActions)
            ? latest.WheelActions
            : wheelActions;

        var browserSites = BrowserSites
            .Select((site, index) => site with { Order = index })
            .ToArray();
        if (BrowserSitesEqual(browserSites, _baseline.BrowserSites))
            browserSites = latest.BrowserSites.ToArray();

        var selectedBrowserSiteId = latest.SelectedBrowserSiteId;
        if (selectedBrowserSiteId is null ||
            !browserSites.Any(site => site.Id == selectedBrowserSiteId))
        {
            selectedBrowserSiteId = browserSites.FirstOrDefault()?.Id;
        }

        var browserReleaseOnClose = BrowserReleaseOnClose != _baseline.BrowserReleaseOnClose
            ? BrowserReleaseOnClose
            : latest.BrowserReleaseOnClose;
        var browserZoomPercent = BrowserZoomPercent != _baseline.BrowserZoomPercent
            ? BrowserZoomPercent
            : latest.BrowserZoomPercent;
        var browserTopmost = BrowserTopmost != _baseline.BrowserTopmost
            ? BrowserTopmost
            : latest.BrowserTopmost;

        var capture = latest.Capture;
        var captureDelay = CaptureDelayIndex switch { 1 => 3, 2 => 5, _ => 0 };
        if (captureDelay != _baseline.Capture.DelaySeconds)
            capture = capture with { DelaySeconds = captureDelay };
        var captureFormat = CaptureFormatIndex == 1 ? ImageFormat.Jpeg : ImageFormat.Png;
        if (captureFormat != _baseline.Capture.Format)
            capture = capture with { Format = captureFormat };
        if (CaptureAutoSave != _baseline.Capture.AutoSave)
            capture = capture with { AutoSave = CaptureAutoSave };
        var captureDirectory = string.IsNullOrWhiteSpace(CaptureSaveDirectory)
            ? null
            : CaptureSaveDirectory;
        if (!string.Equals(captureDirectory, _baseline.Capture.SaveDirectory, StringComparison.Ordinal))
            capture = capture with { SaveDirectory = captureDirectory };
        if (CaptureOpenEditor != _baseline.Capture.OpenEditor)
            capture = capture with { OpenEditor = CaptureOpenEditor };
        if (!string.Equals(AnnotationColor, _baseline.Capture.AnnotationColor, StringComparison.Ordinal))
            capture = capture with { AnnotationColor = AnnotationColor };
        if (StrokeWidthPx != _baseline.Capture.StrokeWidthPx)
            capture = capture with { StrokeWidthPx = StrokeWidthPx };
        if (FontSizePx != _baseline.Capture.FontSizePx)
            capture = capture with { FontSizePx = FontSizePx };

        var hotkeys = new Dictionary<HotkeyAction, HotkeyChord>(latest.Hotkeys);
        MergeHotkey(HotkeyAction.Panel, PanelHotkey);
        MergeHotkey(HotkeyAction.RegionCapture, RegionCaptureHotkey);
        MergeHotkey(HotkeyAction.PauseInput, PauseHotkey);

        return latest with
        {
            General = general,
            Orb = orb,
            Overlay = overlay,
            InputLock = inputLock,
            Launchers = launchers,
            BrowserSites = browserSites,
            SelectedBrowserSiteId = selectedBrowserSiteId,
            BrowserReleaseOnClose = browserReleaseOnClose,
            BrowserZoomPercent = browserZoomPercent,
            BrowserTopmost = browserTopmost,
            Capture = capture,
            Hotkeys = hotkeys,
            Windows = latest.Windows,
            WheelActions = effectiveWheelActions
        };

        void MergeHotkey(HotkeyAction action, string value)
        {
            var baseline = _baseline.Hotkeys.GetValueOrDefault(action);
            var edited = ParseHotkey(value, baseline);
            if (edited != baseline)
                hotkeys[action] = edited;
        }
    }

    private WheelActionKind[] BuildWheelActions()
    {
        var actions = new List<WheelActionKind>();
        Add(ShowRegionCaptureInWheel, WheelActionKind.RegionCapture);
        Add(ShowWindowCaptureInWheel, WheelActionKind.WindowCapture);
        Add(ShowMonitorCaptureInWheel, WheelActionKind.MonitorCapture);
        Add(ShowAiBrowserInWheel, WheelActionKind.AiBrowser);
        Add(ShowNotesInWheel, WheelActionKind.Notes);
        Add(ShowInputLockInWheel, WheelActionKind.InputLock);
        Add(ShowSettingsInWheel, WheelActionKind.Settings);
        return actions.ToArray();

        void Add(bool enabled, WheelActionKind kind)
        {
            if (enabled)
                actions.Add(kind);
        }
    }

    public bool TryBuildCandidate(
        AppSettings latest,
        out AppSettings candidate,
        out string error)
    {
        if (!TryParseHotkey(PanelHotkey, out _))
        {
            candidate = latest;
            error = "快捷面板快捷键格式无效，请使用 Ctrl+Alt+Space 这类格式。";
            return false;
        }
        if (!TryParseHotkey(RegionCaptureHotkey, out _))
        {
            candidate = latest;
            error = "框选截图快捷键格式无效，请使用 Ctrl+Alt+A 这类格式。";
            return false;
        }
        if (!TryParseHotkey(PauseHotkey, out _))
        {
            candidate = latest;
            error = "暂停输入规则快捷键格式无效，请使用 Ctrl+Alt+F12 这类格式。";
            return false;
        }
        if (CaptureAutoSave &&
            (string.IsNullOrWhiteSpace(CaptureSaveDirectory) ||
             !Path.IsPathFullyQualified(CaptureSaveDirectory)))
        {
            candidate = latest;
            error = "启用自动保存时必须选择有效的绝对目录。";
            return false;
        }
        candidate = BuildCandidate(latest);
        error = "";
        return true;
    }

    private static bool LaunchersEqual(
        IEnumerable<LauncherItem> left,
        IEnumerable<LauncherItem> right) =>
        left.SequenceEqual(
            right,
            LauncherItemComparer.Instance);

    private static bool BrowserSitesEqual(
        IEnumerable<BrowserSite> left,
        IEnumerable<BrowserSite> right) =>
        left.SequenceEqual(right);

    private sealed class LauncherItemComparer : IEqualityComparer<LauncherItem>
    {
        public static LauncherItemComparer Instance { get; } = new();

        public bool Equals(LauncherItem? left, LauncherItem? right) =>
            ReferenceEquals(left, right) ||
            left is not null &&
            right is not null &&
            left.Id == right.Id &&
            string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
            left.Kind == right.Kind &&
            string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
            left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
            string.Equals(left.WorkingDirectory, right.WorkingDirectory, StringComparison.Ordinal) &&
            left.Order == right.Order &&
            left.ShowInWheel == right.ShowInWheel;

        public int GetHashCode(LauncherItem value) => HashCode.Combine(
            value.Id,
            value.Name,
            value.Kind,
            value.Path,
            value.Order,
            value.ShowInWheel);
    }

    internal static string FormatHotkey(HotkeyChord chord)
    {
        if (chord.VirtualKey == 0)
            return "";
        var parts = new List<string>();
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (chord.Modifiers.HasFlag(ShortcutModifiers.Win)) parts.Add("Win");
        parts.Add(chord.VirtualKey switch
        {
            0x20 => "Space",
            >= 0x70 and <= 0x87 => $"F{chord.VirtualKey - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)chord.VirtualKey).ToString(),
            >= 0x41 and <= 0x5A => ((char)chord.VirtualKey).ToString(),
            _ => $"0x{chord.VirtualKey:X2}"
        });
        return string.Join("+", parts);
    }

    private static HotkeyChord ParseHotkey(string text, HotkeyChord fallback)
        => TryParseHotkey(text, out var chord) ? chord : fallback;

    private static bool TryParseHotkey(string text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;
        var modifiers = ShortcutModifiers.None;
        uint key = 0;
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ShortcutModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= ShortcutModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ShortcutModifiers.Shift;
                    break;
                case "WIN":
                    modifiers |= ShortcutModifiers.Win;
                    break;
                case "SPACE":
                    key = 0x20;
                    break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetter(part[0]))
                        key = char.ToUpperInvariant(part[0]);
                    else if (part.StartsWith('F') &&
                             int.TryParse(part.AsSpan(1), out var fn) &&
                             fn is >= 1 and <= 24 &&
                             key == 0)
                        key = (uint)(0x6F + fn);
                    else if (part.Length == 1 && char.IsAsciiDigit(part[0]))
                        key = part[0];
                    else
                        return false;
                    break;
            }
        }
        if (key == 0 || modifiers == ShortcutModifiers.None)
            return false;
        chord = new HotkeyChord(modifiers, key);
        return true;
    }
}
