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
    public void OnlySelectedApplicationIdsAndExplicitTitleMatchesPass()
    {
        var rules = new DanmakuSettings { TitleFilter = "标题A\n好友B", SelectedApplications = [new("app-a", "同名应用")] };
        Assert.True(DanmakuBuffer.Matches(new("1", "同名应用", "标题A", "你好", "app-a"), rules));
        Assert.False(DanmakuBuffer.Matches(new("2", "同名应用", "标题A", "你好", "app-b"), rules));
        Assert.False(DanmakuBuffer.Matches(new("3", "同名应用", "标题B", "标题A", "app-a"), rules));
        Assert.False(DanmakuBuffer.Matches(new("4", "TIM", "标题A", "你好", "tim"), rules));
        Assert.True(DanmakuBuffer.Matches(new("5", "任意应用", "标题A", "你好", "new-app"), rules with { ReceiveAllApplications = true }));
        Assert.False(DanmakuBuffer.Matches(new("6", "任意应用", "标题A", "你好", "new-app"), new()));
    }
    [Fact]
    public void ApplicationSelectionIsBoundedAndNormalized()
    {
        var value = new DanmakuSettings { SelectedApplications = [new("app-a", "A"), new("APP-A", "重复"), new("", "坏值")], DisplayMode = (DanmakuDisplayMode)99 }.Normalize();
        Assert.Single(value.SelectedApplications);
        Assert.Equal(DanmakuDisplayMode.Always, value.DisplayMode);
        Assert.False(value.ReceiveAllApplications);
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
