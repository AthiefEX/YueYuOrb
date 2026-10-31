using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using FloatDesk.Core.Models;
namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private string _danmakuToggleHotkey = "";
    [ObservableProperty] private string _danmakuPauseHotkey = "";
    [ObservableProperty] private string _danmakuClearHotkey = "";
    [ObservableProperty] private bool _danmakuEnabled = false;
    [ObservableProperty] private bool _danmakuShowInWheel = false;
    [ObservableProperty] private bool _danmakuReceiveAllApplications;
    [ObservableProperty] private DanmakuDisplayMode _danmakuDisplayMode;
    [ObservableProperty] private bool _danmakuFollowFullscreenMonitor = true;
    public ObservableCollection<NotificationApplicationChoice> DanmakuApplications { get; } = [];
    public IReadOnlyList<NotificationDisplayModeOption> DanmakuDisplayModes { get; } =
        [new(DanmakuDisplayMode.Always, "始终显示"), new(DanmakuDisplayMode.FullscreenOnly, "仅全屏游戏 / 视频时显示"), new(DanmakuDisplayMode.WindowedOnly, "仅非全屏时显示")];
    [ObservableProperty] private bool _danmakuVertical = false;
    [ObservableProperty] private string _danmakuTitleFilter = "";
    [ObservableProperty] private string _danmakuHighlightKeywords = "";
    [ObservableProperty] private string _danmakuColor = "#FFFFFF";
    [ObservableProperty] private string _danmakuHighlightColor = "#FFD166";
    [ObservableProperty] private string _danmakuFontFamily = "Microsoft YaHei UI";
    [ObservableProperty] private double _danmakuFontSize = 20;
    [ObservableProperty] private double _danmakuOpacity = .9;
    [ObservableProperty] private double _danmakuWidthDip = 600;
    [ObservableProperty] private double _danmakuHeightDip = 240;
    [ObservableProperty] private string? _danmakuMonitorId = null;
    [ObservableProperty] private double _danmakuRelativeX = 1;
    [ObservableProperty] private double _danmakuRelativeY = 0;
    [ObservableProperty] private double _danmakuDurationSeconds = 8;
    [ObservableProperty] private bool _danmakuShowTitle = true;
    [ObservableProperty] private bool _danmakuReduceMotion = false;
    public IReadOnlyList<string> DanmakuMonitors { get; } = System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray();
    private void LoadDanmaku(AppSettings settings)
    {
        DanmakuToggleHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.ToggleDanmaku));
        DanmakuPauseHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.PauseDanmaku));
        DanmakuClearHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.ClearDanmaku));
        DanmakuEnabled = settings.Danmaku.Enabled;
        DanmakuShowInWheel = settings.Danmaku.ShowInWheel;
        DanmakuReceiveAllApplications = settings.Danmaku.ReceiveAllApplications;
        DanmakuDisplayMode = settings.Danmaku.DisplayMode;
        DanmakuFollowFullscreenMonitor = settings.Danmaku.FollowFullscreenMonitor;
        foreach (var app in settings.Danmaku.SelectedApplications)
            DanmakuApplications.Add(new(app.Id, app.Name) { IsSelected = true });
        DanmakuVertical = settings.Danmaku.Vertical;
        DanmakuTitleFilter = settings.Danmaku.TitleFilter;
        DanmakuHighlightKeywords = settings.Danmaku.HighlightKeywords;
        DanmakuColor = settings.Danmaku.Color;
        DanmakuHighlightColor = settings.Danmaku.HighlightColor;
        DanmakuFontFamily = settings.Danmaku.FontFamily;
        DanmakuFontSize = settings.Danmaku.FontSize;
        DanmakuOpacity = settings.Danmaku.Opacity;
        DanmakuWidthDip = settings.Danmaku.WidthDip;
        DanmakuHeightDip = settings.Danmaku.HeightDip;
        DanmakuMonitorId = settings.Danmaku.MonitorId;
        DanmakuRelativeX = settings.Danmaku.RelativeX;
        DanmakuRelativeY = settings.Danmaku.RelativeY;
        DanmakuDurationSeconds = settings.Danmaku.DurationSeconds;
        DanmakuShowTitle = settings.Danmaku.ShowTitle;
        DanmakuReduceMotion = settings.Danmaku.ReduceMotion;
    }
    public DanmakuSettings GetDanmakuPreview() => MergeDanmaku(_baseline.Danmaku).Normalize();
    public void RefreshDanmakuApplications(IReadOnlyList<NotificationApplication> applications)
    {
        foreach (var app in applications.Take(256))
        {
            var existing = DanmakuApplications.FirstOrDefault(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) existing.Name = app.Name;
            else DanmakuApplications.Add(new(app.Id, app.Name));
        }
    }
    private IReadOnlyList<NotificationApplication> MergeSelectedApplications(DanmakuSettings latest)
    {
        var selected = latest.SelectedApplications.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        var baselineIds = _baseline.Danmaku.SelectedApplications.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in DanmakuApplications)
        {
            if (choice.IsSelected == baselineIds.Contains(choice.Id)) continue;
            if (choice.IsSelected) selected[choice.Id] = new(choice.Id, choice.Name);
            else selected.Remove(choice.Id);
        }
        var result = selected.Values.ToArray();
        return result.SequenceEqual(latest.SelectedApplications) ? latest.SelectedApplications : result;
    }
    private void MergeDanmakuKeys(Dictionary<HotkeyAction, HotkeyChord> keys)
    {
        foreach (var (action, text) in new[] { (HotkeyAction.ToggleDanmaku, DanmakuToggleHotkey), (HotkeyAction.PauseDanmaku, DanmakuPauseHotkey), (HotkeyAction.ClearDanmaku, DanmakuClearHotkey) })
        {
            if (text == FormatHotkey(_baseline.Hotkeys.GetValueOrDefault(action))) continue;
            if (string.IsNullOrWhiteSpace(text)) keys.Remove(action);
            else if (TryParseHotkey(text, out var chord)) keys[action] = chord;
            else throw new InvalidOperationException("弹幕快捷键格式无效，请使用 Ctrl+Alt+F9 等格式。");
        }
    }
    private DanmakuSettings MergeDanmaku(DanmakuSettings latest)
    {
        var baseline = _baseline.Danmaku;
        return (latest with {
            Enabled = DanmakuEnabled != baseline.Enabled ? DanmakuEnabled : latest.Enabled,
            ShowInWheel = DanmakuShowInWheel != baseline.ShowInWheel ? DanmakuShowInWheel : latest.ShowInWheel,
            ReceiveAllApplications = DanmakuReceiveAllApplications != baseline.ReceiveAllApplications ? DanmakuReceiveAllApplications : latest.ReceiveAllApplications,
            DisplayMode = DanmakuDisplayMode != baseline.DisplayMode ? DanmakuDisplayMode : latest.DisplayMode,
            FollowFullscreenMonitor = DanmakuFollowFullscreenMonitor != baseline.FollowFullscreenMonitor ? DanmakuFollowFullscreenMonitor : latest.FollowFullscreenMonitor,
            SelectedApplications = MergeSelectedApplications(latest),
            Vertical = DanmakuVertical != baseline.Vertical ? DanmakuVertical : latest.Vertical,
            TitleFilter = DanmakuTitleFilter != baseline.TitleFilter ? DanmakuTitleFilter : latest.TitleFilter,
            HighlightKeywords = DanmakuHighlightKeywords != baseline.HighlightKeywords ? DanmakuHighlightKeywords : latest.HighlightKeywords,
            Color = DanmakuColor != baseline.Color ? DanmakuColor : latest.Color,
            HighlightColor = DanmakuHighlightColor != baseline.HighlightColor ? DanmakuHighlightColor : latest.HighlightColor,
            FontFamily = DanmakuFontFamily != baseline.FontFamily ? DanmakuFontFamily : latest.FontFamily,
            FontSize = DanmakuFontSize != baseline.FontSize ? DanmakuFontSize : latest.FontSize,
            Opacity = DanmakuOpacity != baseline.Opacity ? DanmakuOpacity : latest.Opacity,
            WidthDip = DanmakuWidthDip != baseline.WidthDip ? DanmakuWidthDip : latest.WidthDip,
            HeightDip = DanmakuHeightDip != baseline.HeightDip ? DanmakuHeightDip : latest.HeightDip,
            MonitorId = DanmakuMonitorId != baseline.MonitorId ? DanmakuMonitorId : latest.MonitorId,
            RelativeX = DanmakuRelativeX != baseline.RelativeX ? DanmakuRelativeX : latest.RelativeX,
            RelativeY = DanmakuRelativeY != baseline.RelativeY ? DanmakuRelativeY : latest.RelativeY,
            DurationSeconds = DanmakuDurationSeconds != baseline.DurationSeconds ? DanmakuDurationSeconds : latest.DurationSeconds,
            ShowTitle = DanmakuShowTitle != baseline.ShowTitle ? DanmakuShowTitle : latest.ShowTitle,
            ReduceMotion = DanmakuReduceMotion != baseline.ReduceMotion ? DanmakuReduceMotion : latest.ReduceMotion,
        }).Normalize();
    }
}

public partial class NotificationApplicationChoice(string id, string name) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private bool _isSelected;
}
public sealed record NotificationDisplayModeOption(DanmakuDisplayMode Mode, string Label);
