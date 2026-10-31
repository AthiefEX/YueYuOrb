using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class DanmakuTests
{
    [Fact]
    public void DefaultsArePrivateAndCompatible()
    {
        var value = AppSettings.CreateDefault();
        Assert.False(value.Danmaku.Enabled);
        Assert.Equal(1, value.SchemaVersion);
        Assert.Equal(600, value.Danmaku.WidthDip);
        Assert.Equal(240, value.Danmaku.HeightDip);
    }

    [Fact]
    public void OnlySelectedPlatformsAndExplicitTitleMatchesPass()
    {
        var rules = new DanmakuSettings { TitleFilter = "群A\n好友B" };
        Assert.True(DanmakuBuffer.Matches(new("1", "TIM", "群A", "你好"), rules));
        Assert.False(DanmakuBuffer.Matches(new("2", "TIM", "群B", "群A"), rules));
        Assert.False(DanmakuBuffer.Matches(new("3", "Other", "群A", "你好"), rules));
        Assert.False(DanmakuBuffer.Matches(new("4", "微信", "群A", "你好"), rules with { WeChat = false }));
    }

    [Fact]
    public void QueueIsBoundedExpiresAndClearsDeduplication()
    {
        var queue = new DanmakuBuffer();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 101; i++) queue.Enqueue(new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "TIM", "群", "内容"), now);
        Assert.Equal(100, queue.Count);
        Assert.Equal("1", queue.Take(now)!.Id);
        Assert.Null(queue.Take(now.AddSeconds(31)));
        queue.Clear();
        Assert.True(queue.Enqueue(new("1", "TIM", "群", "内容"), now));
        Assert.False(queue.Enqueue(new("1", "TIM", "群", "内容"), now));
    }

    [Fact]
    public void InvalidGeometryAndAppearanceAreNormalized()
    {
        var value = new DanmakuSettings { WidthDip = double.NaN, HeightDip = -1, Opacity = double.PositiveInfinity, FontSize = 999, Color = "bad" }.Normalize();
        Assert.Equal(600, value.WidthDip);
        Assert.Equal(80, value.HeightDip);
        Assert.Equal(.9, value.Opacity);
        Assert.Equal(96, value.FontSize);
        Assert.Equal("#FFFFFF", value.Color);
    }
}
