using FloatDesk.Core.Models;
using FloatDesk.Windows.Brightness;

namespace FloatDesk.Windows.Tests;

public sealed class BrightnessServiceTests
{
    [Fact]
    public async Task NewTopologyCannotReuseAnOldInFlightSnapshot()
    {
        using var backend = new SlowDiscovery();
        using var service = new WindowsBrightnessService(backend);
        var old = service.GetSnapshotAsync(default);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.NotifyTopologyChanged();
        try { await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetSnapshotAsync(default)); }
        finally { backend.Release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => old);
    }
    private sealed class SlowDiscovery : IBrightnessBackend, IDisposable
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<BrightnessDevice> Enumerate() { Started.TrySetResult(); Release.Wait(); return []; }
        public BrightnessWriteResult SetPercent(string id, int percent) => new(id, percent, null);
        public void Dispose() { Release.Set(); Release.Dispose(); }
    }
    [Fact]
    public async Task BlockedDeviceDoesNotBlockOtherDevicesOrCreateRetryWorkers()
    {
        using var backend = new BlockingBackend();
        using var service = new WindowsBrightnessService(backend);
        var first = service.SetPercentAsync("a", 20, default);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Equal(60, (await service.SetPercentAsync("b", 60, default)).ActualPercent);
            Assert.Contains("超时", (await first).Error);
            Assert.NotNull((await service.SetPercentAsync("a", 80, default)).Error);
            Assert.Equal(1, backend.Calls);
        }
        finally { backend.Release.Set(); }
    }

    [Fact]
    public async Task LatestRequestSurvivesTimedOutWriteAndRunsAfterItFinishes()
    {
        using var backend = new BlockingBackend();
        using var service = new WindowsBrightnessService(backend);
        var first = service.SetPercentAsync("a", 0, default);
        await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull((await first).Error);
        var recovery = service.SetPercentAsync("a", 50, default);
        backend.Release.Set();
        var result = await recovery;
        Assert.Equal(50, result.ActualPercent);
        Assert.Null(result.Error);
        Assert.Equal(2, backend.Calls);
    }

    private sealed class BlockingBackend : IBrightnessBackend, IDisposable
    {
        public readonly ManualResetEventSlim Release = new();
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public IReadOnlyList<BrightnessDevice> Enumerate() => [];
        public BrightnessWriteResult SetPercent(string id, int percent)
        {
            if (id == "a") { Interlocked.Increment(ref Calls); Started.TrySetResult(); Release.Wait(); }
            return new(id, percent, null);
        }
        public void Dispose() { Release.Set(); }
    }

    [Fact]
    public async Task DevicesFailIndependentlyAndWritesReturnActualReadback()
    {
        using var service = new WindowsBrightnessService(new Backend());
        var snapshot = await service.GetSnapshotAsync(default);
        Assert.Equal(2, snapshot.Devices.Count);
        var result = await service.SetPercentAsync("a", 51, default);
        Assert.Equal(50, result.ActualPercent);
        Assert.Null(result.Error);
        Assert.NotNull((await service.SetPercentAsync("b", 30, default)).Error);
        Assert.NotNull((await service.SetPercentAsync("missing", 30, default)).Error);
    }

    [Fact]
    public async Task CancellationDoesNotWriteAndDisposeRejectsWork()
    {
        var backend = new Backend();
        var service = new WindowsBrightnessService(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetPercentAsync("a", 5, cancellation.Token));
        Assert.Equal(0, backend.Writes);
        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetSnapshotAsync(default));
    }

    private sealed class Backend : IBrightnessBackend
    {
        public int Writes;
        public IReadOnlyList<BrightnessDevice> Enumerate() => [new("a", "A", 40, null, true), new("b", "B", null, "不支持", false)];
        public BrightnessWriteResult SetPercent(string id, int percent)
        {
            Interlocked.Increment(ref Writes);
            return id == "a" ? new(id, percent / 10 * 10, null) : new(id, null, "不支持");
        }
    }
}
