using FloatDesk.App.Danmaku;
using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DanmakuRuntimeTests
{
    [Theory]
    [InlineData(DanmakuDisplayMode.Always, false, 1)]
    [InlineData(DanmakuDisplayMode.Always, true, 1)]
    [InlineData(DanmakuDisplayMode.FullscreenOnly, false, 0)]
    [InlineData(DanmakuDisplayMode.FullscreenOnly, true, 1)]
    [InlineData(DanmakuDisplayMode.WindowedOnly, false, 1)]
    [InlineData(DanmakuDisplayMode.WindowedOnly, true, 0)]
    public async Task DisplayModesRespectTheCurrentContext(DanmakuDisplayMode mode, bool fullscreen, int expected)
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true, DisplayMode = mode });
        runtime.SetDisplayContext(fullscreen, new nint(123), "screen-b");
        source.Next.SetResult([new("1", "应用", "标题", "通知")]);
        await runtime.PollAsync(); Assert.Equal(expected, surface.Presented);
    }
    [Fact]
    public void TopologyRefreshPreservesEffectiveFullscreenMonitor()
    {
        var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(new FakeSource(), surface);
        runtime.ApplySettings(new() { MonitorId = "screen-a", FollowFullscreenMonitor = true });
        runtime.SetDisplayContext(true, new nint(123), "screen-b");
        var applications = surface.Applications;
        runtime.RefreshDisplayPlacement();
        Assert.Equal(applications + 1, surface.Applications);
        Assert.Equal("screen-b", surface.LastMonitor);
        runtime.SetDisplayContext(false, new nint(124), null);
        Assert.Equal("screen-a", surface.LastMonitor);
    }
    [Fact]
    public async Task ReadStartedOutsideFullscreenCannotAppearAfterEnteringFullscreen()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true, DisplayMode = DanmakuDisplayMode.FullscreenOnly });
        var pending = runtime.PollAsync();
        runtime.SetDisplayContext(true, new nint(123), "screen-b");
        source.Next.SetResult([new("late", "应用", "标题", "非全屏时到达")]);
        await pending;
        Assert.Equal(0, surface.Presented);
    }
    [Fact]
    public void FirstDefaultSettingsStillInitializeTheSurface()
    {
        var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(new FakeSource(), surface);
        runtime.ApplySettings(new());
        Assert.Equal(1, surface.Applications);
    }
    [Fact]
    public async Task FullscreenOnlyDropsWindowedNotificationsAndDoesNotReassertOnIdle()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true, DisplayMode = DanmakuDisplayMode.FullscreenOnly });
        source.Next.SetResult([]);
        source.Batches.Enqueue([new("windowed", "应用", "标题", "不应积压")]);
        source.Batches.Enqueue([new("fullscreen", "应用", "标题", "全屏新消息")]);
        await runtime.PollAsync(); Assert.Equal(0, surface.Presented);
        runtime.SetDisplayContext(true, new nint(123), "screen-b");
        await runtime.PollAsync(); Assert.Equal(1, surface.Presented);
        var refresh = surface.Refreshes;
        runtime.SetDisplayContext(true, new nint(123), "screen-b");
        Assert.Equal(refresh, surface.Refreshes);
        runtime.SetDisplayContext(true, new nint(124), "screen-b");
        Assert.True(surface.Refreshes > refresh);
        runtime.SetDisplayContext(false, new nint(125), null);
        var presented = surface.Presented;
        await runtime.PollAsync(); Assert.Equal(presented, surface.Presented);
    }
    [Fact]
    public void LocalTestDoesNotReadNotificationsAndRespectsPrivacyPauses()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        Assert.True(runtime.PlayTestMessage()); Assert.Equal(1, surface.Presented); Assert.Equal(0, source.Polls);
        runtime.SetPaused(true); Assert.False(runtime.PlayTestMessage());
        runtime.SetPaused(false); runtime.SetSessionLocked(true); Assert.False(runtime.PlayTestMessage());
        runtime.SetSessionLocked(false); runtime.SetSubtitleSessionActive(true); Assert.False(runtime.PlayTestMessage());
    }
    [Fact]
    public async Task RevokedAccessDropsPendingMessagesAndSubtitleSessionSuppressesDelivery()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true });
        source.Next.SetResult([new("1", "TIM", "群", "第一条"), new("2", "TIM", "群", "第二条")]);
        await runtime.PollAsync(); Assert.Equal(1, surface.Presented);
        source.HasAccess = false;
        var cleared = surface.Cleared;
        await runtime.PollAsync();
        Assert.Equal(1, surface.Presented); Assert.True(surface.Cleared > cleared);
        source.HasAccess = true;
        runtime.SetSubtitleSessionActive(true);
        var polls = source.Polls;
        await runtime.PollAsync(); Assert.Equal(polls, source.Polls);
        runtime.SetSubtitleSessionActive(false); runtime.SetPaused(true);
        await runtime.PollAsync(); Assert.Equal(polls, source.Polls);
    }
    [Fact]
    public async Task DisableRejectsLateResultAndClearsDisplay()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true });
        var pending = runtime.PollAsync();
        runtime.ApplySettings(new() { Enabled = false });
        source.Next.SetResult([new("1", "TIM", "群", "你好")]);
        await pending;
        Assert.Equal(0, surface.Presented);
        Assert.True(surface.Cleared > 0);
    }

    [Fact]
    public async Task OverlappingCaptureLeasesAndLockKeepMessagesHidden()
    {
        var source = new FakeSource(); var surface = new FakeSurface();
        using var runtime = new DanmakuRuntime(source, surface);
        runtime.ApplySettings(new() { Enabled = true, ReceiveAllApplications = true });
        var first = runtime.SuspendForCapture(); var second = runtime.SuspendForCapture();
        first.Dispose(); await runtime.PollAsync(); Assert.Equal(0, source.Polls);
        runtime.SetSessionLocked(true); second.Dispose();
        await runtime.PollAsync(); Assert.Equal(0, source.Polls);
        runtime.SetSessionLocked(false);
        source.Next.SetResult([new("1", "TIM", "群", "新消息")]);
        await runtime.PollAsync(); Assert.Equal(1, source.Polls); Assert.Equal(1, surface.Presented);
    }
    private sealed class FakeSource : IDanmakuNotificationSource
    {
        public TaskCompletionSource<IReadOnlyList<DanmakuMessage>> Next { get; } = new();
        public Queue<IReadOnlyList<DanmakuMessage>> Batches { get; } = new();
        public int Polls { get; private set; }
        public bool HasAccess { get; set; } = true;
        public string Status => "测试";
        public Task<bool> RequestAccessAsync() => Task.FromResult(true);
        public Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken token) { Polls++; return Batches.TryDequeue(out var batch) ? Task.FromResult(batch) : Next.Task; }
        public void Reset() { }
    }
    private sealed class FakeSurface : IDanmakuSurface
    {
        public int Presented { get; private set; }
        public int Cleared { get; private set; }
        public int Refreshes { get; private set; }
        public int Applications { get; private set; }
        public string? LastMonitor { get; private set; }
        public bool CanPresent => true;
        public void ApplySettings(DanmakuSettings settings) { Applications++; LastMonitor = settings.MonitorId; }
        public void Present(DanmakuMessage message, bool highlighted) => Presented++;
        public void Clear() => Cleared++;
        public void RefreshZOrder() => Refreshes++;
    }
}
