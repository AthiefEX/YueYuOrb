namespace FloatDesk.Core.Danmaku;

/// <summary>Coalesces notification events without repeatedly querying the OS while idle.</summary>
public sealed class NotificationReadGate
{
    private readonly object _sync = new();
    private HashSet<uint> _known = [];
    private readonly HashSet<uint> _pending = [];
    private bool _baseline = true, _reading;
    public bool HasPending { get { lock (_sync) return _baseline || _pending.Count > 0; } }
    public void Added(uint id)
    {
        lock (_sync) { if (!_known.Contains(id) && _pending.Count < 4096) _pending.Add(id); }
    }
    public void Removed(uint id) { lock (_sync) { _known.Remove(id); _pending.Remove(id); } }
    public bool TryBeginRead()
    {
        lock (_sync)
        {
            if (_reading || (!_baseline && _pending.Count == 0)) return false;
            _pending.Clear(); // Consume this batch; events arriving during the read form the next batch.
            _reading = true; return true;
        }
    }
    public void Complete(IEnumerable<uint> ids)
    {
        lock (_sync)
        {
            _known = ids.Take(4096).ToHashSet(); _pending.ExceptWith(_known);
            _baseline = false; _reading = false;
        }
    }
    public void Reset()
    {
        lock (_sync) { _known.Clear(); _pending.Clear(); _baseline = true; _reading = false; }
    }
}
