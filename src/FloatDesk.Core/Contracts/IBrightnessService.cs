using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IBrightnessService : IDisposable
{
    Task<BrightnessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<BrightnessWriteResult> SetPercentAsync(string deviceId, int percent, CancellationToken cancellationToken);
}
