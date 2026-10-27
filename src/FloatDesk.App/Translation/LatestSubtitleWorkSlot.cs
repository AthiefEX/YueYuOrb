namespace FloatDesk.App.Translation;

/// <summary>
/// Holds at most one unclaimed work item. Publishing a newer item transfers the
/// replaced item's cleanup responsibility back to the slot.
/// </summary>
public sealed class LatestSubtitleWorkSlot<T> : IDisposable
    where T : class, IDisposable
{
    private readonly object _sync = new();
    private T? _pending;
    private TaskCompletionSource _signal = CreateSignal();
    private bool _disposed;

    public void Publish(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        T? replaced = null;
        TaskCompletionSource? signal = null;
        lock (_sync)
        {
            if (_disposed)
            {
                replaced = item;
            }
            else
            {
                replaced = _pending;
                _pending = item;
                signal = _signal;
                _signal = CreateSignal();
            }
        }

        replaced?.Dispose();
        signal?.TrySetResult();
    }

    public async Task<T?> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_sync)
            {
                if (_disposed)
                    return null;
                if (_pending is { } pending)
                {
                    _pending = null;
                    return pending;
                }
                wait = _signal.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        T? pending;
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = _pending;
            _pending = null;
            signal = _signal;
            _signal = CreateSignal();
        }

        pending?.Dispose();
        signal.TrySetResult();
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
