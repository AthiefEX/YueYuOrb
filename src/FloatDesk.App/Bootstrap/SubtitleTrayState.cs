namespace FloatDesk.App;

internal readonly record struct SubtitleTrayState(
    bool Running,
    bool Paused)
{
    public bool CommandsEnabled => Running;

    public string PauseHeader =>
        Paused ? "继续字幕翻译" : "暂停字幕翻译";
}
