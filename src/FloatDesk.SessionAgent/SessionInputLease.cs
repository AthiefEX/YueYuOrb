namespace FloatDesk.SessionAgent;

public sealed class SessionInputLease
{
    private readonly object _sync = new();
    private DateTimeOffset? _expiresAt;

    public bool IsExpired
    {
        get
        {
            lock (_sync)
            {
                return _expiresAt is null ||
                    _expiresAt <= DateTimeOffset.UtcNow;
            }
        }
    }

    public bool Acquire(int seconds)
    {
        if (seconds is < 1 or > 5)
            return false;
        lock (_sync)
        {
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
            return true;
        }
    }

    public bool Renew()
    {
        lock (_sync)
        {
            if (_expiresAt is null)
                return false;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
            return true;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            _expiresAt = null;
        }
    }

    public bool Expire()
    {
        lock (_sync)
        {
            if (_expiresAt is not null &&
                _expiresAt > DateTimeOffset.UtcNow)
            {
                return false;
            }

            var wasActive = _expiresAt is not null;
            _expiresAt = null;
            return wasActive;
        }
    }
}
