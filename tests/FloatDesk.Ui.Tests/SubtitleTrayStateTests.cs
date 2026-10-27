using FloatDesk.App;

namespace FloatDesk.Ui.Tests;

public sealed class SubtitleTrayStateTests
{
    [Fact]
    public void SubtitleTrayStateReflectsRunningAndPausedState()
    {
        Assert.True(new SubtitleTrayState(true, false).CommandsEnabled);
        Assert.Equal(
            "暂停字幕翻译",
            new SubtitleTrayState(true, false).PauseHeader);
        Assert.Equal(
            "继续字幕翻译",
            new SubtitleTrayState(true, true).PauseHeader);
        Assert.False(new SubtitleTrayState(false, false).CommandsEnabled);
    }
}
