namespace FloatDesk.App.Danmaku;

/// <summary>UI-thread-only state. Contains counters/status only, never notification text.</summary>
public static class DanmakuLiveStatus
{
    public static string Text { get; internal set; } = "等待运行状态";
    public static Func<bool>? PlayTest { get; internal set; }
}
