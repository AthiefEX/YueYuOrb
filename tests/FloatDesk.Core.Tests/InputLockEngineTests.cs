using System.Globalization;
using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class InputLockEngineTests
{
    private static readonly nint TargetHkl = 0x04090409;
    private static readonly nint ChineseHkl = 0x08040804;

    [Fact]
    public async Task ConfiguredKlidReachesBackend()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options("00010409"),
            CancellationToken.None);

        Assert.Equal(["00010409"], backend.ResolveCalls);
    }

    [Fact]
    public async Task NoRuleDoesNotCallBackend()
    {
        var backend = new FakeInputBackend();
        await using var engine = NewEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.Empty(backend.ResolveCalls);
        Assert.Equal(0, backend.ActivateCount);
        Assert.Equal(InputLockState.Idle, engine.Status.State);
    }

    [Fact]
    public async Task SuccessfulReadbackAndGuardAreRequiredForVerified()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(InputLockState.Verified, engine.Status.State);
        Assert.Equal(1, guard.EnableCount);
        Assert.True(guard.IsActive);
    }

    [Fact]
    public async Task RepeatedVerifiedPollDoesNotRepublishTransitionStates()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);
        var target = Snapshot(1, 100);
        await engine.UpdateAsync(
            target,
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        var publishedStates = new List<InputLockState>();
        engine.StatusChanged += status =>
            publishedStates.Add(status.State);

        await engine.UpdateAsync(
            target,
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Empty(publishedStates);
    }

    [Fact]
    public async Task FirstLockSavesAndManualUnlockRestoresPreviousHkl()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 100),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal([ChineseHkl], backend.RestoreCalls);
        Assert.False(engine.HasPendingRestore);
        Assert.Equal(InputLockState.Idle, engine.Status.State);
    }

    [Fact]
    public async Task LeavingRuleApplicationRestoresPreviousHkl()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);
        var rule = Lock(@"C:\Apps\editor.exe");

        await engine.UpdateAsync(
            Snapshot(1, 100, @"C:\Apps\editor.exe"),
            Context(manual: false, auto: true),
            [rule],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200, @"C:\Apps\other.exe"),
            Context(manual: false, auto: true),
            [rule],
            Options(),
            CancellationToken.None);

        Assert.Equal([ChineseHkl], backend.RestoreCalls);
        Assert.False(engine.HasPendingRestore);
    }

    [Fact]
    public async Task TargetGoneRestoreIsAppliedOnceToNextEligibleForeground()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        backend.RestoreResults.Enqueue(OperationResult.Fail(
            "TargetGone",
            "目标窗口已关闭。"));
        await using var engine = NewEngine(backend);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.True(engine.HasPendingRestore);

        await engine.UpdateAsync(
            Snapshot(3, 300),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(4, 400),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(2, backend.RestoreCalls.Count);
        Assert.False(engine.HasPendingRestore);
    }

    [Fact]
    public async Task MissingTargetLayoutNeverEnablesGuard()
    {
        var backend = new FakeInputBackend
        {
            ResolutionFailure = LayoutResolution.Fail(
                "LayoutUnavailable",
                "Windows 尚未安装目标键盘布局。")
        };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(InputLockState.Unavailable, engine.Status.State);
        Assert.Equal(0, guard.EnableCount);
        Assert.Equal(0, backend.ActivateCount);
    }

    [Fact]
    public async Task GuardStartupFailureIsDegradedNotVerified()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard
        {
            EnableResult = OperationResult.Fail(
                "HookUnavailable",
                "快捷键拦截不可用。")
        };
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(InputLockState.Degraded, engine.Status.State);
        Assert.True(engine.IsActive);
        Assert.False(guard.IsActive);
    }

    [Fact]
    public async Task AccessDeniedMapsToDeniedWithoutClaimingLock()
    {
        var backend = new FakeInputBackend
        {
            ApplyActivation = false,
            ActivationResult = OperationResult.Fail(
                "AccessDenied",
                "目标以更高权限运行。")
        };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(InputLockState.Denied, engine.Status.State);
        Assert.Equal(0, guard.EnableCount);
    }

    [Fact]
    public async Task RuleToRuleTransitionKeepsLockContinuous()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);
        var editor = Lock(@"C:\Apps\editor.exe");
        var game = Lock(@"C:\Apps\game.exe");

        await engine.UpdateAsync(
            Snapshot(1, 100, @"C:\Apps\editor.exe"),
            Context(manual: false, auto: true),
            [editor, game],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200, @"C:\Apps\game.exe"),
            Context(manual: false, auto: true),
            [editor, game],
            Options(),
            CancellationToken.None);

        Assert.Empty(backend.RestoreCalls);
        Assert.Equal(0, guard.DisableCount);
        Assert.Equal(InputLockState.Verified, engine.Status.State);
    }

    [Fact]
    public async Task ForegroundChangeClearsPendingShortcutCandidates()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100, @"C:\Apps\editor.exe"),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200, @"C:\Apps\game.exe"),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(2, guard.ClearPendingLearningCount);
    }

    [Fact]
    public async Task TemporaryPreserveDisablesGuardButRetainsRestore()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(2, 200, @"C:\Apps\editor.exe", own: true),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.False(guard.IsActive);
        Assert.True(engine.HasPendingRestore);
        Assert.Empty(backend.RestoreCalls);

        await engine.UpdateAsync(
            Snapshot(3, 300),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal([ChineseHkl], backend.RestoreCalls);
        Assert.False(engine.HasPendingRestore);
    }

    [Fact]
    public async Task StaleRestoreCompletionCannotClearPendingState()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        await using var engine = NewEngine(backend, guard);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        backend.HeldRestore = new TaskCompletionSource<OperationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var release = engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);
        await backend.RestoreStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var relock = engine.UpdateAsync(
            Snapshot(3, 300),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        backend.HeldRestore.SetResult(OperationResult.Ok());
        await Task.WhenAll(release, relock);

        Assert.True(engine.HasPendingRestore);
        Assert.Equal(InputLockState.Verified, engine.Status.State);
    }

    [Fact]
    public async Task NewEpochDoesNotVerifyOldResult()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);

        var staleTask = engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        var currentTask = engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await Task.WhenAll(staleTask, currentTask);

        Assert.Equal(InputLockState.Verified, engine.Status.State);
        Assert.Equal(2, engine.Status.Epoch);
    }

    [Fact]
    public async Task RetryRequestsOccurAtCumulativeZeroOneAndThreeHundredMilliseconds()
    {
        var clock = new RecordingTimeProvider();
        var backend = new FakeInputBackend
        {
            Clock = clock,
            ApplyActivation = false
        };
        await using var engine = NewEngine(
            backend,
            new FakeKeyboardSwitchGuard(),
            clock);

        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal([0, 100, 300], clock.RequestOffsets);
        Assert.Equal(InputLockState.Unsupported, engine.Status.State);
    }

    [Fact]
    public async Task NewEpochGetsAnIndependentRequestBudget()
    {
        var clock = new RecordingTimeProvider();
        var backend = new FakeInputBackend
        {
            Clock = clock,
            ApplyActivation = false
        };
        await using var engine = NewEngine(
            backend,
            new FakeKeyboardSwitchGuard(),
            clock);

        for (var index = 0; index < 3; index++)
        {
            await engine.UpdateAsync(
                Snapshot(1, 100),
                Context(manual: true),
                [],
                Options(),
                CancellationToken.None);
        }
        await engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(9, backend.ActivateCount);
        Assert.Equal(2, engine.Status.Epoch);
    }

    [Fact]
    public async Task StaleNonLockingUpdateCannotReplaceCurrentStatus()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        await using var engine = NewEngine(backend);

        await engine.UpdateAsync(
            Snapshot(2, 200),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: false),
            [],
            Options(),
            CancellationToken.None);

        Assert.Equal(InputLockState.Verified, engine.Status.State);
        Assert.Equal(2, engine.Status.Epoch);
    }

    [Fact]
    public async Task DisposalDisablesGuardAndAttemptsRestore()
    {
        var backend = new FakeInputBackend { ApplyActivation = true };
        var guard = new FakeKeyboardSwitchGuard();
        var engine = NewEngine(backend, guard);
        await engine.UpdateAsync(
            Snapshot(1, 100),
            Context(manual: true),
            [],
            Options(),
            CancellationToken.None);

        await engine.DisposeAsync();

        Assert.Equal([ChineseHkl], backend.RestoreCalls);
        Assert.False(guard.IsActive);
        Assert.Equal(1, guard.DisposeCount);
    }

    [Fact]
    public async Task PendingLeaseIsReleasedExactlyOnce()
    {
        await using var engine = NewEngine(new FakeInputBackend());
        var lease = engine.PauseForCapture();

        Assert.True(engine.IsCapturePaused);
        lease.Dispose();
        lease.Dispose();
        Assert.False(engine.IsCapturePaused);
    }

    private static InputLockEngine NewEngine(
        FakeInputBackend backend,
        FakeKeyboardSwitchGuard? guard = null,
        TimeProvider? timeProvider = null) =>
        new(backend, guard ?? new FakeKeyboardSwitchGuard(), timeProvider);

    private static InputContext Context(
        bool manual,
        bool auto = false) =>
        new(false, false, false, manual, auto);

    private static InputLockOptions Options(string klid = "00000409") =>
        new(klid);

    private static ForegroundSnapshot Snapshot(
        long epoch,
        nint hwnd,
        string path = @"C:\Apps\editor.exe",
        bool own = false) =>
        new(
            epoch,
            hwnd,
            hwnd + 1,
            42,
            43,
            DateTimeOffset.UtcNow,
            path,
            own);

    private static AppRule Lock(string path) =>
        new(Guid.NewGuid(), path, InputAction.LockEnglish, true);

    private sealed class FakeInputBackend : IInputBackend
    {
        public RecordingTimeProvider? Clock { get; init; }
        public bool ApplyActivation { get; init; }
        public OperationResult ActivationResult { get; init; } =
            OperationResult.Ok();
        public LayoutResolution? ResolutionFailure { get; init; }
        public List<string> ResolveCalls { get; } = [];
        public int ActivateCount { get; private set; }
        public List<nint> RestoreCalls { get; } = [];
        public Queue<OperationResult> RestoreResults { get; } = [];
        public TaskCompletionSource RestoreStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OperationResult>? HeldRestore { get; set; }
        public nint CurrentHkl { get; set; } = ChineseHkl;

        public Task<LayoutResolution> ResolveAsync(
            string klid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolveCalls.Add(klid);
            return Task.FromResult(
                ResolutionFailure ??
                LayoutResolution.Ok(new InputLayoutTarget(
                    TargetHkl,
                    klid,
                    0x0409)));
        }

        public Task<LayoutObservation?> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<LayoutObservation?>(Observe(CurrentHkl));
        }

        public Task<OperationResult> ActivateAsync(
            ForegroundSnapshot target,
            InputLayoutTarget layout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivateCount++;
            Clock?.RequestOffsets.Add(Clock.ElapsedMilliseconds);
            if (ApplyActivation)
                CurrentHkl = layout.Hkl;
            return Task.FromResult(ActivationResult);
        }

        public async Task<OperationResult> RestoreAsync(
            ForegroundSnapshot target,
            nint hkl,
            CancellationToken cancellationToken)
        {
            RestoreCalls.Add(hkl);
            OperationResult result;
            if (HeldRestore is not null)
            {
                RestoreStarted.TrySetResult();
                result = await HeldRestore.Task.WaitAsync(cancellationToken);
            }
            else
            {
                result = RestoreResults.Count > 0
                    ? RestoreResults.Dequeue()
                    : OperationResult.Ok();
            }
            if (result.Success)
                CurrentHkl = hkl;
            return result;
        }

        private static LayoutObservation Observe(nint hkl)
        {
            var language = (uint)(long)hkl & 0xFFFF;
            var isEnglish = (language & 0x3FF) == 0x09;
            return new LayoutObservation(
                hkl,
                language.ToString("X8", CultureInfo.InvariantCulture),
                language,
                isEnglish,
                language == 0x0804);
        }
    }

    private sealed class FakeKeyboardSwitchGuard : IKeyboardSwitchGuard
    {
        public bool IsActive { get; private set; }
        public int EnableCount { get; private set; }
        public int DisableCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ClearPendingLearningCount { get; private set; }
        public OperationResult EnableResult { get; init; } =
            OperationResult.Ok();

        public OperationResult Enable(
            InputLayoutTarget target,
            IReadOnlyList<KeyboardSwitchChord>? additionalShortcuts = null)
        {
            EnableCount++;
            IsActive = EnableResult.Success;
            return EnableResult;
        }

        public void Disable()
        {
            DisableCount++;
            IsActive = false;
        }

        public void ObserveLayout(LayoutObservation observation)
        {
        }

        public void ClearPendingLearning() =>
            ClearPendingLearningCount++;

        public void Dispose()
        {
            DisposeCount++;
            IsActive = false;
        }
    }

    private sealed class RecordingTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _start =
            new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        private DateTimeOffset _utcNow =
            new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

        public List<int> RequestOffsets { get; } = [];
        public int ElapsedMilliseconds =>
            (int)(_utcNow - _start).TotalMilliseconds;

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
}
