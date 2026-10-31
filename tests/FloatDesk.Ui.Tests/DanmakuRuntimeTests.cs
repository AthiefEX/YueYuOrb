using FloatDesk.App.Danmaku;
using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DanmakuRuntimeTests
{
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
        runtime.ApplySettings(new() { Enabled = true });
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
        runtime.ApplySettings(new() { Enabled = true });
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
        runtime.ApplySettings(new() { Enabled = true });
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
        public int Polls { get; private set; }
        public bool HasAccess { get; set; } = true;
        public string Status => "测试";
        public Task<bool> RequestAccessAsync() => Task.FromResult(true);
        public Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken token) { Polls++; return Next.Task; }
        public void Reset() { }
    }
    private sealed class FakeSurface : IDanmakuSurface
    {
        public int Presented { get; private set; }
        public int Cleared { get; private set; }
        public bool CanPresent => true;
        public void ApplySettings(DanmakuSettings settings) { }
        public void Present(DanmakuMessage message, bool highlighted) => Presented++;
        public void Clear() => Cleared++;
    }
}
