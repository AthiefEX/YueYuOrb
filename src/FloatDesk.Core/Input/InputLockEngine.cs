using FloatDesk.Core.Models;

namespace FloatDesk.Core.Input;

public sealed class InputLockEngine : IAsyncDisposable
{
    private readonly IInputBackend _backend;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Queue<DateTimeOffset> _requestTimes = new();
    private long _currentEpoch;
    private long _requestEpoch = -1;
    private int _capturePauseCount;
    private DateTimeOffset _cooldownUntil;
    private bool _disposed;

    public InputLockEngine(IInputBackend backend, TimeProvider? timeProvider = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<InputStatus>? StatusChanged;
    public InputStatus Status { get; private set; } = new(InputLockState.Idle, 0, "等待前台应用");
    public bool IsCapturePaused => Volatile.Read(ref _capturePauseCount) > 0;

    public async Task UpdateAsync(
        ForegroundSnapshot target,
        InputContext context,
        IReadOnlyList<AppRule> rules,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (target.Epoch < _currentEpoch)
                return;
            _currentEpoch = Math.Max(_currentEpoch, target.Epoch);
        }

        var decision = InputPolicy.Decide(target, context, rules);
        if (decision.Action != InputAction.LockEnglish)
        {
            SetStatus(new(
                IsCapturePaused ? InputLockState.Paused : InputLockState.Idle,
                target.Epoch,
                decision.Reason));
            return;
        }

        SetStatus(new(InputLockState.Applying, target.Epoch, "正在切换英文布局"));
        try
        {
            var delays = new[] { 0, 100, 200 };
            OperationResult? lastFailure = null;
            foreach (var delay in delays)
            {
                if (delay > 0)
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(delay),
                        _timeProvider,
                        cancellationToken);
                EnsureCurrent(target);
                var before = await _backend.ReadAsync(target, cancellationToken);
                EnsureCurrent(target);
                if (before is { IsTargetUsEnglish: true, IsIme: false })
                {
                    SetStatus(new(
                        InputLockState.Verified,
                        target.Epoch,
                        "已验证英文布局"));
                    return;
                }

                if (!TryAcquireRequestBudget(target.Epoch, out var cooldown))
                {
                    SetStatus(new(
                        InputLockState.Unsupported,
                        target.Epoch,
                        $"应用持续拒绝或覆盖切换；冷却至 {cooldown:HH:mm:ss}"));
                    return;
                }

                var result = await _backend.RequestUsEnglishAsync(
                    target,
                    cancellationToken);
                EnsureCurrent(target);
                if (!result.Success)
                {
                    lastFailure = result;
                    if (result.ErrorCode is "AccessDenied" or "LayoutUnavailable")
                        break;
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
            var observation = await _backend.ReadAsync(target, cancellationToken);
            EnsureCurrent(target);
            if (observation is { IsTargetUsEnglish: true, IsIme: false })
            {
                SetStatus(new(
                    InputLockState.Verified,
                    target.Epoch,
                    "已验证英文布局"));
                return;
            }

            var state = lastFailure?.ErrorCode == "AccessDenied"
                ? InputLockState.Denied
                : lastFailure?.ErrorCode is "TargetGone" or "TargetChanged"
                    ? InputLockState.Unavailable
                    : lastFailure?.ErrorCode == "LayoutUnavailable"
                        ? InputLockState.Unavailable
                        : InputLockState.Unsupported;
            SetStatus(new(
                state,
                target.Epoch,
                lastFailure?.Message ?? "目标应用未接受英文布局"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
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
                SetStatus(new(InputLockState.Unavailable, target.Epoch, "输入法接口不可用"));
        }
    }

    private void EnsureCurrent(ForegroundSnapshot target)
    {
        lock (_sync)
        {
            if (target.Epoch != _currentEpoch)
                throw new OperationCanceledException(
                    "The foreground target changed.");
        }
    }

    private bool TryAcquireRequestBudget(long epoch, out DateTimeOffset cooldown)
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
        lock (_sync)
        {
            if (status.Epoch < _currentEpoch)
                return;
            Status = status;
            StatusChanged?.Invoke(status);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        StatusChanged = null;
        return ValueTask.CompletedTask;
    }

    private sealed class Releaser(InputLockEngine owner) : IDisposable
    {
        private InputLockEngine? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseCapture();
    }
}
