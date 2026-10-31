using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;
namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private string _danmakuToggleHotkey = "";
    [ObservableProperty] private string _danmakuPauseHotkey = "";
    [ObservableProperty] private string _danmakuClearHotkey = "";
    [ObservableProperty] private bool _danmakuEnabled = false;
    [ObservableProperty] private bool _danmakuShowInWheel = false;
    [ObservableProperty] private bool _danmakuQQ = true;
    [ObservableProperty] private bool _danmakuTIM = true;
    [ObservableProperty] private bool _danmakuWeChat = true;
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
        DanmakuQQ = settings.Danmaku.QQ;
        DanmakuTIM = settings.Danmaku.TIM;
        DanmakuWeChat = settings.Danmaku.WeChat;
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
            QQ = DanmakuQQ != baseline.QQ ? DanmakuQQ : latest.QQ,
            TIM = DanmakuTIM != baseline.TIM ? DanmakuTIM : latest.TIM,
            WeChat = DanmakuWeChat != baseline.WeChat ? DanmakuWeChat : latest.WeChat,
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
