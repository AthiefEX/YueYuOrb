namespace FloatDesk.App.Coordination;

public sealed class CaptureSessionGate : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Waiter> _waiters = new();
    private Lease? _current;
    private bool _disposed;

    public Lease? TryEnter()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_current is not null)
                return null;
            _current = new Lease(this);
            return _current;
        }
    }

    public Task<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_current is null)
            {
                _current = new Lease(this);
                return Task.FromResult(_current);
            }

            var waiter = new Waiter(cancellationToken);
            _waiters.Enqueue(waiter);
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(
                    static state =>
                    {
                        var (gate, current) =
                            ((CaptureSessionGate, Waiter))state!;
                        gate.CancelWaiter(current);
                    },
                    (this, waiter));
            }
            return waiter.Completion.Task;
        }
    }

    private void CancelWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private void Release(Lease lease)
    {
        List<Waiter>? registrationsToDispose = null;
        lock (_sync)
        {
            if (!ReferenceEquals(_current, lease))
                return;
            _current = null;
            while (_waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                registrationsToDispose ??= [];
                registrationsToDispose.Add(waiter);
                if (waiter.CancellationToken.IsCancellationRequested)
                {
                    waiter.Completion.TrySetCanceled(
                        waiter.CancellationToken);
                    continue;
                }

                _current = new Lease(this);
                if (waiter.Completion.TrySetResult(_current))
                    break;
                _current = null;
            }
        }

        if (registrationsToDispose is not null)
        {
            foreach (var waiter in registrationsToDispose)
                waiter.Registration.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        Waiter[] waiters;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _current = null;
            waiters = _waiters.ToArray();
            _waiters.Clear();
            foreach (var waiter in waiters)
            {
                waiter.Completion.TrySetException(
                    new ObjectDisposedException(nameof(CaptureSessionGate)));
            }
        }

        foreach (var waiter in waiters)
            waiter.Registration.Dispose();
    }

    public sealed class Lease : IDisposable
    {
        private CaptureSessionGate? _owner;

        internal Lease(CaptureSessionGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(this);
        }
    }

    private sealed class Waiter(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } =
            cancellationToken;
        public TaskCompletionSource<Lease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
}
