namespace FloatDesk.Core.Contracts;

public interface IDesktopTaskExecutionLedger
{
    Task<bool> TryBeginAsync(
        string key,
        CancellationToken cancellationToken);
}
