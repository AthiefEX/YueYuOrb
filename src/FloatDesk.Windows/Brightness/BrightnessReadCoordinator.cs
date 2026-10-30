using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Brightness;

public sealed class BrightnessReadCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Generation, Task<BrightnessDevice> Task)> _reads = new();
    public async Task<BrightnessDevice> ReadAsync(string id, long generation, Func<BrightnessDevice> read, TimeSpan timeout)
    {
        (long Generation, Task<BrightnessDevice> Task) entry;
        lock (_gate)
        {
            if (!_reads.TryGetValue(id, out entry) || entry.Task.IsCompleted)
                _reads[id] = entry = (generation, Task.Run(read));
        }
        try
        {
            if (entry.Generation != generation) throw new InvalidOperationException();
            return await entry.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch { return new(id, id, null, "显示器响应超时或配置已变化。", false); }
    }
}
