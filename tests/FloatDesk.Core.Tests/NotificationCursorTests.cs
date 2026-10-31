using FloatDesk.Core.Danmaku;

namespace FloatDesk.Core.Tests;

public sealed class NotificationCursorTests
{
    [Fact]
    public void InitialAndResumedSnapshotsNeverReplayOldNotifications()
    {
        var cursor = new NotificationCursor();
        Assert.Empty(cursor.Accept(["old"]));
        Assert.Equal(["new"], cursor.Accept(["old", "new"]));
        Assert.Empty(cursor.Accept(["old", "new"]));
        cursor.Reset();
        Assert.Empty(cursor.Accept(["old", "new", "duringPause"]));
        Assert.Equal(["afterResume"], cursor.Accept(["old", "new", "duringPause", "afterResume"]));
    }

    [Theory]
    [InlineData("TIM", "TIM")]
    [InlineData("腾讯QQ", "QQ")]
    [InlineData("WeChat", "微信")]
    [InlineData("微信", "微信")]
    [InlineData("Unknown QQ app", null)]
    public void OnlyKnownApplicationNamesAreAccepted(string name, string? platform) =>
        Assert.Equal(platform, NotificationCursor.Platform(name));
}
