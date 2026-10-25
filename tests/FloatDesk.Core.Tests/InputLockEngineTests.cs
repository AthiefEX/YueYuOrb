using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class InputLockEngineTests
{
    [Fact]
    public async Task NewEpochDoesNotVerifyOldResult()
    {
        var backend = new FakeInputBackend { RequestStartsUs = false };
        await using var engine = new InputLockEngine(backend);
        var first = Snapshot(1, 100);
        var second = Snapshot(2, 200);

        var staleTask = engine.UpdateAsync(
            first,
            Context(manual: true),
            [],
            CancellationToken.None);
        await Task.Yield();
        var currentTask = engine.UpdateAsync(
            second,
            Context(manual: true),
            [],
            CancellationToken.None);
        await Task.WhenAll(staleTask, currentTask);

        Assert.NotEqual(InputLockState.Verified, engine.Status.State);
        Assert.Equal(2, engine.Status.Epoch);
    }

    [Fact]
    public async Task NoRuleDoesNotCallBackend()
    {
        var backend = new FakeInputBackend { RequestStartsUs = false };
        await using var engine = new InputLockEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: false),
            [],
            CancellationToken.None);

        Assert.Equal(0, backend.RequestCount);
        Assert.Equal(InputLockState.Idle, engine.Status.State);
    }

    [Fact]
    public async Task SuccessfulReadbackIsTheOnlyVerifiedState()
    {
        var backend = new FakeInputBackend { RequestStartsUs = true };
        await using var engine = new InputLockEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            CancellationToken.None);

        Assert.Equal(InputLockState.Verified, engine.Status.State);
    }

    [Fact]
    public async Task PendingLeaseIsReleasedExactlyOnce()
    {
        await using var engine = new InputLockEngine(new FakeInputBackend());
        var lease = engine.PauseForCapture();

        Assert.True(engine.IsCapturePaused);
        lease.Dispose();
        lease.Dispose();
        Assert.False(engine.IsCapturePaused);
    }

    [Fact]
    public async Task RetryRequestsOccurAtCumulativeZeroOneAndThreeHundredMilliseconds()
    {
        var clock = new RecordingTimeProvider();
        var backend = new FakeInputBackend(clock) { RequestStartsUs = false };
        await using var engine = new InputLockEngine(backend, clock);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            CancellationToken.None);

        Assert.Equal([0, 100, 300], clock.RequestOffsets);
    }

    [Fact]
    public async Task NewEpochGetsAnIndependentRequestBudget()
    {
        var clock = new RecordingTimeProvider();
        var backend = new FakeInputBackend(clock) { RequestStartsUs = false };
        await using var engine = new InputLockEngine(backend, clock);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: true),
            [],
            CancellationToken.None);

        Assert.Equal(9, backend.RequestCount);
        Assert.Equal(2, engine.Status.Epoch);
    }

    [Fact]
    public async Task StaleNonLockingUpdateCannotReplaceCurrentStatus()
    {
        var backend = new FakeInputBackend { RequestStartsUs = true };
        await using var engine = new InputLockEngine(backend);

        await engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: true),
            [],
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: false),
            [],
            CancellationToken.None);

        Assert.Equal(InputLockState.Verified, engine.Status.State);
        Assert.Equal(2, engine.Status.Epoch);
    }

    private static InputContext Context(bool manual) =>
        new(false, false, false, manual, true);

    private static ForegroundSnapshot Snapshot(long epoch, nint hwnd) =>
        new(epoch, hwnd, hwnd + 1, 42, 43, DateTimeOffset.UtcNow,
            @"C:\Apps\editor.exe", false);

    private sealed class FakeInputBackend : IInputBackend
    {
        public bool RequestStartsUs { get; set; }
        public int RequestCount { get; private set; }
        private readonly RecordingTimeProvider? _clock;
        private readonly List<int> _requestOffsets = [];
        private ForegroundSnapshot? _requested;

        public FakeInputBackend(RecordingTimeProvider? clock = null) =>
            _clock = clock;

        public Task<LayoutObservation?> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken ct)
        {
            var isUs = _requested == target && RequestStartsUs;
            return Task.FromResult<LayoutObservation?>(
                new LayoutObservation(0x04090409, isUs, false));
        }

        public Task<OperationResult> RequestUsEnglishAsync(
            ForegroundSnapshot target,
            CancellationToken ct)
        {
            RequestCount++;
            _clock?.RequestOffsets.Add(_clock.ElapsedMilliseconds);
            _requested = target;
            return Task.FromResult(
                new OperationResult(true, null, null));
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow =
            new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

        public List<int> RequestOffsets { get; } = [];

        public int ElapsedMilliseconds => (int)(_utcNow - new DateTimeOffset(
            2026,
            10,
            25,
            0,
            0,
            0,
            TimeSpan.Zero)).TotalMilliseconds;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
                _utcNow += dueTime;
            callback(state);
            return NoopTimer.Instance;
        }
    }

    private sealed class NoopTimer : ITimer
    {
        public static NoopTimer Instance { get; } = new();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
