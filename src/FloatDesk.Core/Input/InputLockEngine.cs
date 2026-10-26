using FloatDesk.Core.Models;

namespace FloatDesk.Core.Input;

public sealed class InputLockEngine : IAsyncDisposable
{
    private readonly IInputBackend _backend;
    private readonly IKeyboardSwitchGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _requestTimes = new();
    private InputStatus _status =
        new(InputLockState.Idle, 0, "等待前台应用");
    private long _currentEpoch;
    private long _requestEpoch = -1;
    private int _capturePauseCount;
    private DateTimeOffset _cooldownUntil;
    private nint _restoreHkl;
    private string? _targetKlid;
    private InputLayoutTarget? _resolvedTarget;
    private ForegroundSnapshot? _lastLockedTarget;
    private bool _lockSessionActive;
    private bool _guardAttempted;
    private long _guardObservationEpoch = -1;
    private string? _guardFailureMessage;
    private bool _disposed;

    public InputLockEngine(
        IInputBackend backend,
        IKeyboardSwitchGuard guard,
        TimeProvider? timeProvider = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<InputStatus>? StatusChanged;

    public InputStatus Status
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
                return _lockSessionActive;
        }
    }

    public bool HasPendingRestore
    {
        get
        {
            lock (_sync)
                return _restoreHkl != 0;
        }
    }

    public bool IsCapturePaused => Volatile.Read(ref _capturePauseCount) > 0;

    public async Task UpdateAsync(
        ForegroundSnapshot target,
        InputContext context,
        IReadOnlyList<AppRule> rules,
        InputLockOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (target.Epoch < _currentEpoch)
                return;
            _currentEpoch = Math.Max(_currentEpoch, target.Epoch);
        }

        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            EnsureCurrent(target);
            var decision = InputPolicy.Decide(target, context, rules);
            if (decision.Action != InputAction.LockEnglish)
            {
                await ReleaseAsync(
                    target,
                    decision,
                    cancellationToken);
                return;
            }

            await ApplyLockAsync(
                target,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            // A newer foreground epoch superseded this request. Its stale
            // result or error must never become the current input status.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            if (target.Epoch == _currentEpoch)
            {
                DisableGuard();
                SetLockSessionActive(false);
                SetStatus(new(
                    InputLockState.Unavailable,
                    target.Epoch,
                    "输入法接口不可用"));
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task ApplyLockAsync(
        ForegroundSnapshot target,
        InputLockOptions options,
        CancellationToken cancellationToken)
    {
        var targetLayout = await ResolveTargetAsync(
            options.TargetKlid,
            target,
            cancellationToken);
        if (targetLayout is null)
            return;

        if (_guardObservationEpoch != target.Epoch)
        {
            _guard.ClearPendingLearning();
            _guardObservationEpoch = target.Epoch;
        }
        BeginLockSession(target);

        var observation = await _backend.ReadAsync(
            target,
            cancellationToken);
        EnsureCurrent(target);
        if (observation is null)
        {
            DisableGuard();
            SetLockSessionActive(false);
            SetStatus(new(
                InputLockState.Unavailable,
                target.Epoch,
                "无法读取前台线程的键盘布局"));
            return;
        }

        _guard.ObserveLayout(observation);
        if (TryVerify(target, observation, targetLayout, options))
            return;

        SetStatus(new(
            InputLockState.Applying,
            target.Epoch,
            "正在切换到目标英文键盘"));
        SaveRestoreLayout(observation.Hkl);

        var delays = new[] { 0, 100, 200 };
        OperationResult? lastFailure = null;
        foreach (var delay in delays)
        {
            if (delay > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(delay),
                    _timeProvider,
                    cancellationToken);
                EnsureCurrent(target);
                observation = await _backend.ReadAsync(
                    target,
                    cancellationToken);
                EnsureCurrent(target);
                if (observation is null)
                {
                    DisableGuard();
                    SetLockSessionActive(false);
                    SetStatus(new(
                        InputLockState.Unavailable,
                        target.Epoch,
                        "前台线程在切换完成前结束"));
                    return;
                }

                _guard.ObserveLayout(observation);
                if (TryVerify(
                        target,
                        observation,
                        targetLayout,
                        options))
                {
                    return;
                }
                SaveRestoreLayout(observation.Hkl);
            }

            if (!TryAcquireRequestBudget(target.Epoch, out var cooldown))
            {
                SetStatus(new(
                    InputLockState.Unsupported,
                    target.Epoch,
                    $"应用持续拒绝或覆盖切换；冷却至 {cooldown:HH:mm:ss}"));
                return;
            }

            var result = await _backend.ActivateAsync(
                target,
                targetLayout,
                cancellationToken);
            EnsureCurrent(target);
            if (!result.Success)
            {
                lastFailure = result;
                if (result.ErrorCode is
                    "AccessDenied" or "LayoutUnavailable")
                {
                    break;
                }
            }
            else
            {
                lastFailure = null;
            }
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(100),
            _timeProvider,
            cancellationToken);
        EnsureCurrent(target);
        observation = await _backend.ReadAsync(
            target,
            cancellationToken);
        EnsureCurrent(target);
        if (observation is not null &&
            TryVerify(target, observation, targetLayout, options))
        {
            return;
        }

        var state = lastFailure?.ErrorCode switch
        {
            "AccessDenied" => InputLockState.Denied,
            "TargetGone" or "TargetChanged" or "LayoutUnavailable" =>
                InputLockState.Unavailable,
            _ => InputLockState.Unsupported
        };
        SetStatus(new(
            state,
            target.Epoch,
            lastFailure?.Message ?? "目标应用未接受目标英文布局"));
    }

    private bool TryVerify(
        ForegroundSnapshot target,
        LayoutObservation observation,
        InputLayoutTarget targetLayout,
        InputLockOptions options)
    {
        if (observation.Hkl != targetLayout.Hkl || observation.IsIme)
            return false;

        if (!_guard.IsActive && !_guardAttempted)
        {
            _guardAttempted = true;
            var enabled = _guard.Enable(
                targetLayout,
                options.AdditionalShortcuts);
            if (!enabled.Success || !_guard.IsActive)
            {
                _guardFailureMessage =
                    enabled.Message ?? "键盘快捷键拦截未能启动";
                SetStatus(new(
                    InputLockState.Degraded,
                    target.Epoch,
                    $"英文布局已验证，但{_guardFailureMessage}"));
                return true;
            }
            _guardFailureMessage = null;
        }

        if (_guard.IsActive)
        {
            SetStatus(new(
                InputLockState.Verified,
                target.Epoch,
                "已验证英文键盘，切换快捷键拦截已开启"));
        }
        else
        {
            SetStatus(new(
                InputLockState.Degraded,
                target.Epoch,
                $"英文布局已验证，但{_guardFailureMessage ?? "快捷键拦截不可用"}"));
        }
        return true;
    }

    private async Task<InputLayoutTarget?> ResolveTargetAsync(
        string klid,
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        var normalized = (klid ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        var disableGuard = false;
        lock (_sync)
        {
            if (!string.Equals(
                    _targetKlid,
                    normalized,
                    StringComparison.Ordinal))
            {
                _targetKlid = normalized;
                _resolvedTarget = null;
                _guardAttempted = false;
                _guardFailureMessage = null;
                disableGuard = _guard.IsActive;
            }
            else if (_resolvedTarget is not null)
            {
                return _resolvedTarget;
            }
        }

        if (disableGuard)
            DisableGuard();

        var resolution = await _backend.ResolveAsync(
            normalized,
            cancellationToken);
        EnsureCurrent(target);
        if (!resolution.Success || resolution.Target is null)
        {
            DisableGuard();
            SetLockSessionActive(false);
            SetStatus(new(
                InputLockState.Unavailable,
                target.Epoch,
                resolution.Message ?? "目标键盘布局不可用"));
            return null;
        }

        lock (_sync)
            _resolvedTarget = resolution.Target;
        return resolution.Target;
    }

    private async Task ReleaseAsync(
        ForegroundSnapshot target,
        InputDecision decision,
        CancellationToken cancellationToken)
    {
        DisableGuard();
        SetLockSessionActive(false);

        var state = IsCapturePaused
            ? InputLockState.Paused
            : InputLockState.Idle;
        if (decision.PreserveLockState)
        {
            SetStatus(new(state, target.Epoch, decision.Reason));
            return;
        }

        nint restoreHkl;
        lock (_sync)
            restoreHkl = _restoreHkl;
        if (restoreHkl == 0)
        {
            SetStatus(new(state, target.Epoch, decision.Reason));
            return;
        }

        if (!IsRestoreEligible(target))
        {
            SetStatus(new(
                state,
                target.Epoch,
                "等待可恢复的外部前台窗口"));
            return;
        }

        var result = await _backend.RestoreAsync(
            target,
            restoreHkl,
            cancellationToken);
        if (target.Epoch != _currentEpoch)
            return;
        if (result.Success)
        {
            lock (_sync)
                _restoreHkl = 0;
            SetStatus(new(
                InputLockState.Idle,
                target.Epoch,
                "已恢复锁定前的键盘布局"));
            return;
        }

        if (result.ErrorCode is "TargetGone" or "TargetChanged")
        {
            SetStatus(new(
                InputLockState.Idle,
                target.Epoch,
                "等待下一个前台窗口恢复键盘布局"));
            return;
        }

        SetStatus(new(
            InputLockState.Unavailable,
            target.Epoch,
            result.Message ?? "无法恢复锁定前的键盘布局"));
    }

    private void BeginLockSession(ForegroundSnapshot target)
    {
        lock (_sync)
        {
            if (!_lockSessionActive)
            {
                _lockSessionActive = true;
                _guardAttempted = false;
                _guardFailureMessage = null;
                _requestEpoch = -1;
                _requestTimes.Clear();
                _cooldownUntil = default;
            }
            _lastLockedTarget = target;
        }
    }

    private void SaveRestoreLayout(nint hkl)
    {
        if (hkl == 0)
            return;
        lock (_sync)
        {
            if (_restoreHkl == 0)
                _restoreHkl = hkl;
        }
    }

    private void DisableGuard()
    {
        _guard.Disable();
        lock (_sync)
        {
            _guardAttempted = false;
            _guardFailureMessage = null;
        }
    }

    private void SetLockSessionActive(bool active)
    {
        lock (_sync)
            _lockSessionActive = active;
    }

    private void EnsureCurrent(ForegroundSnapshot target)
    {
        lock (_sync)
        {
            if (target.Epoch != _currentEpoch)
            {
                throw new OperationCanceledException(
                    "The foreground target changed.");
            }
        }
    }

    private bool TryAcquireRequestBudget(
        long epoch,
        out DateTimeOffset cooldown)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (_requestEpoch != epoch)
            {
                _requestEpoch = epoch;
                _requestTimes.Clear();
                _cooldownUntil = default;
            }
            if (now < _cooldownUntil)
            {
                cooldown = _cooldownUntil.ToLocalTime();
                return false;
            }
            while (_requestTimes.Count > 0 &&
                   now - _requestTimes.Peek() >= TimeSpan.FromSeconds(2))
            {
                _requestTimes.Dequeue();
            }
            if (_requestTimes.Count >= 6)
            {
                _cooldownUntil = now.AddSeconds(5);
                _requestTimes.Clear();
                cooldown = _cooldownUntil.ToLocalTime();
                return false;
            }
            _requestTimes.Enqueue(now);
            cooldown = default;
            return true;
        }
    }

    private static bool IsRestoreEligible(ForegroundSnapshot target) =>
        target.TopLevelHwnd != 0 &&
        !target.IsOwnInteractive &&
        !string.IsNullOrWhiteSpace(target.ExePath);

    public IDisposable PauseForCapture()
    {
        Interlocked.Increment(ref _capturePauseCount);
        return new Releaser(this);
    }

    private void ReleaseCapture()
    {
        if (Interlocked.Decrement(ref _capturePauseCount) < 0)
            Interlocked.Exchange(ref _capturePauseCount, 0);
    }

    private void SetStatus(InputStatus status)
    {
        InputStatus? publish = null;
        lock (_sync)
        {
            if (status.Epoch < _currentEpoch)
                return;
            if (_status == status)
                return;
            _status = status;
            publish = status;
        }
        if (publish is not null)
            StatusChanged?.Invoke(publish);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _updateGate.WaitAsync();
        try
        {
            DisableGuard();
            nint restoreHkl;
            ForegroundSnapshot? lastTarget;
            lock (_sync)
            {
                restoreHkl = _restoreHkl;
                lastTarget = _lastLockedTarget;
            }
            if (restoreHkl != 0 &&
                lastTarget is not null &&
                IsRestoreEligible(lastTarget))
            {
                try
                {
                    var result = await _backend.RestoreAsync(
                        lastTarget,
                        restoreHkl,
                        CancellationToken.None);
                    if (result.Success)
                    {
                        lock (_sync)
                            _restoreHkl = 0;
                    }
                }
                catch
                {
                    // Shutdown restoration is best effort.
                }
            }
            StatusChanged = null;
            _guard.Dispose();
        }
        finally
        {
            _updateGate.Release();
            _updateGate.Dispose();
        }
    }

    private sealed class Releaser(InputLockEngine owner) : IDisposable
    {
        private InputLockEngine? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseCapture();
    }
}
