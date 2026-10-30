using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

internal sealed class PopupPlacementSaveQueue(Func<WindowPlacement, Task> save)
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    internal Task Completion { get { lock (_gate) return _tail; } }
    internal async Task DrainAfterClosingAsync(Action closeWindows, CancellationToken cancellationToken)
    {
        closeWindows();
        await Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    internal Task Enqueue(WindowPlacement placement)
    {
        lock (_gate)
        {
            var previous = _tail;
            return _tail = Task.Run(async () =>
            {
                try { await previous.ConfigureAwait(false); } catch (Exception) { /* A failed save must not prevent newer placements. */ }
                await save(placement).ConfigureAwait(false);
            });
        }
    }
}
