namespace FloatDesk.App.Coordination;

public sealed class CaptureSessionGate : IDisposable
{
    private readonly object _sync = new();
    private bool _entered;
    private bool _disposed;

    public Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entered)
                _entered = true;
            else
                return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public void Release()
    {
        lock (_sync)
        {
            if (_entered)
            {
                _entered = false;
                return;
            }
            if (_disposed)
                return;
        }
        throw new SynchronizationLockException(
            "The capture session gate is not currently held.");
    }

    public void Dispose()
    {
        lock (_sync)
            _disposed = true;
    }
}
