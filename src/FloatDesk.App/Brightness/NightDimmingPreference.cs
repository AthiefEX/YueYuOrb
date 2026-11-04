using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Brightness;

internal sealed class NightDimmingPreference(ISettingsCoordinator settings)
{
    private readonly object _gate = new();
    private int? _preview;
    private long _revision;
    private Task _tail = Task.CompletedTask;
    public void Preview(int value)
    {
        lock (_gate) { _preview = Math.Clamp(value, 0, 90); _revision++; }
    }
    public int Resolve()
    {
        lock (_gate) return _preview ?? settings.Current.Brightness.NightDimmingPercent;
    }
    public Task<OperationResult> Commit(int value)
    {
        lock (_gate)
        {
            var previous = _tail;
            var revision = _revision;
            var pending = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                var result = await settings.UpdateAsync(current => current with
                {
                    Brightness = current.Brightness with { NightDimmingPercent = Math.Clamp(value, 0, 90) }
                }, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                    if (revision == _revision) _preview = null;
                return result;
            });
            _tail = pending;
            return pending;
        }
    }
}
