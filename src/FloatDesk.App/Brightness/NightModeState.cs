namespace FloatDesk.App.Brightness;

public sealed class NightModeState
{
    private int _pauseDepth;
    public bool IsEnabled { get; private set; }
    public bool IsVisible => IsEnabled && _pauseDepth == 0;
    public int DimmingPercent { get; private set; } = 40;
    public event Action? Changed;
    public void SetEnabled(bool value) { IsEnabled = value; Changed?.Invoke(); }
    public void SetDimmingPercent(int value) { DimmingPercent = Math.Clamp(value, 0, 90); Changed?.Invoke(); }
    public IDisposable Suspend()
    {
        _pauseDepth++;
        Changed?.Invoke();
        return new Lease(this);
    }
    private sealed class Lease(NightModeState owner) : IDisposable
    {
        private NightModeState? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } state) return;
            state._pauseDepth--;
            state.Changed?.Invoke();
        }
    }
}
