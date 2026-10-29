namespace FloatDesk.Core.Geometry;

public sealed class OrbEdgeCollapseState
{
    private TimeSpan? _outsideSince;
    private int _edge;

    public bool Update(bool enabled, int edge, bool pointerInside, bool blocked, TimeSpan now)
    {
        if (!enabled || edge == 0 || pointerInside || blocked || edge != _edge)
            _outsideSince = null;
        _edge = edge;
        if (!enabled || edge == 0 || pointerInside || blocked)
            return false;
        _outsideSince ??= now;
        return now - _outsideSince.Value >= TimeSpan.FromMilliseconds(800);
    }
}
