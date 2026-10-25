namespace FloatDesk.Core.Geometry;

public sealed class OrbDragMotion
{
    private PixelPoint _pointerDown;
    private PixelRect _windowDown;
    private int _thresholdPixels;
    private bool _active;
    private bool _canDrag;

    public bool IsDragging { get; private set; }

    public void Begin(
        PixelPoint position,
        PixelRect window,
        int thresholdPixels,
        bool canDrag = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdPixels);

        _pointerDown = position;
        _windowDown = window;
        _thresholdPixels = thresholdPixels;
        _active = true;
        _canDrag = canDrag;
        IsDragging = false;
    }

    public bool TryUpdate(PixelPoint position, out PixelRect window)
    {
        window = _windowDown;
        if (!_active)
            return false;

        var deltaX = checked(position.X - _pointerDown.X);
        var deltaY = checked(position.Y - _pointerDown.Y);
        if (!IsDragging &&
            Math.Abs(deltaX) < _thresholdPixels &&
            Math.Abs(deltaY) < _thresholdPixels)
        {
            return false;
        }

        IsDragging = true;
        if (!_canDrag)
            return false;

        window = new PixelRect(
            checked(_windowDown.Left + deltaX),
            checked(_windowDown.Top + deltaY),
            _windowDown.Width,
            _windowDown.Height);
        return true;
    }

    public void Reset()
    {
        _active = false;
        _canDrag = false;
        IsDragging = false;
    }
}
