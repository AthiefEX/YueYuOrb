using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Audio;
namespace FloatDesk.Windows.Tests;
public sealed class AudioServiceTests
{
    [Fact]
    public async Task SerializesAllOperationsOnOneMtaThreadAndForwardsEndpointValues()
    {
        var backend = new RecordingBackend();
        using var service = new WindowsAudioService(backend);
        var calls = Enumerable.Range(0, 24).Select(_ => service.GetSnapshotAsync(CancellationToken.None)).ToArray();
        await Task.WhenAll(calls);
        await service.SetDefaultDeviceAsync("headset", CancellationToken.None);
        await service.SetVolumeAsync("speakers", .37, CancellationToken.None);
        await service.SetMuteAsync("headset", true, CancellationToken.None);
        Assert.All(calls, call => Assert.Equal(backend.Snapshot, call.Result));
        Assert.Single(backend.Threads.Distinct());
        Assert.All(backend.Apartments, state => Assert.Equal(ApartmentState.MTA, state));
        Assert.Equal(1, backend.MaxConcurrent);
        Assert.Equal(["default:headset", "volume:speakers:0.37", "mute:headset:True"], backend.Writes);
    }
    [Fact]
    public async Task CancellationBeforeExecutionNeverTouchesBackend()
    {
        var backend = new RecordingBackend();
        using var service = new WindowsAudioService(backend);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetSnapshotAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetDefaultDeviceAsync("a", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetVolumeAsync("a", .4, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetMuteAsync("a", true, canceled.Token));
        Assert.Empty(backend.Threads);
        Assert.Empty(backend.Writes);
    }
    [Fact]
    public async Task QueuedCancellationPreventsWriteAndDisposeWaitsForInflightRead()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend { OnRead = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } };
        var service = new WindowsAudioService(backend);
        try
        {
            var read = service.GetSnapshotAsync(CancellationToken.None);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            using var canceled = new CancellationTokenSource();
            var write = service.SetMuteAsync("a", true, canceled.Token);
            canceled.Cancel();
            var disposal = Task.Run(service.Dispose);
            Assert.False(disposal.IsCompleted);
            release.Set();
            await read;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(backend.Writes);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetSnapshotAsync(CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.SetMuteAsync("a", false, CancellationToken.None));
            service.Dispose();
        }
        finally { release.Set(); service.Dispose(); }
    }
    [Fact]
    public async Task NativeFailureReachesCallerAndWorkerRemainsUsable()
    {
        var error = Assert.IsType<COMException>(Marshal.GetExceptionForHR(unchecked((int)0x80070490)));
        var backend = new RecordingBackend { OnRead = () => throw error };
        using var service = new WindowsAudioService(backend);
        Assert.Same(error, await Assert.ThrowsAsync<COMException>(() => service.GetSnapshotAsync(CancellationToken.None)));
        backend.OnRead = null;
        Assert.Equal(backend.Snapshot, await service.GetSnapshotAsync(CancellationToken.None));
    }
    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task InvalidVolumeDoesNotReachNativeCode(double volume)
    {
        var backend = new RecordingBackend();
        using var service = new WindowsAudioService(backend);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SetVolumeAsync("a", volume, CancellationToken.None));
        Assert.Empty(backend.Writes);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a\0b")]
    public async Task InvalidEndpointIdDoesNotReachNativeCode(string? id)
    {
        var backend = new RecordingBackend();
        using var service = new WindowsAudioService(backend);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetDefaultDeviceAsync(id!, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetVolumeAsync(id!, .5, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SetMuteAsync(id!, true, CancellationToken.None));
        Assert.Empty(backend.Writes);
    }
    private sealed class RecordingBackend : IAudioBackend
    {
        public AudioSnapshot Snapshot { get; } = new([new("a", "Speakers")], "a", .5, true);
        public ConcurrentQueue<int> Threads { get; } = new();
        public ConcurrentQueue<ApartmentState> Apartments { get; } = new();
        public List<string> Writes { get; } = [];
        public Action? OnRead { get; set; }
        private int concurrent;
        public int MaxConcurrent { get; private set; }
        public AudioSnapshot GetSnapshot(CancellationToken token)
        {
            Record();
            MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref concurrent));
            try { OnRead?.Invoke(); Thread.SpinWait(10000); return Snapshot; }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        public void SetDefaultDevice(string deviceId, CancellationToken token) { Record(); Writes.Add($"default:{deviceId}"); }
        public void SetVolume(string deviceId, double volume, CancellationToken token) { Record(); Writes.Add(FormattableString.Invariant($"volume:{deviceId}:{volume}")); }
        public void SetMute(string deviceId, bool muted, CancellationToken token) { Record(); Writes.Add($"mute:{deviceId}:{muted}"); }
        private void Record() { Threads.Enqueue(Environment.CurrentManagedThreadId); Apartments.Enqueue(Thread.CurrentThread.GetApartmentState()); }
    }
}

