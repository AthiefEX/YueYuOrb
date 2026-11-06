using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IStartupExecutionLedger
{
    Task<bool> TryBeginAsync(
        StartupExecutionKey key,
        CancellationToken cancellationToken);
}

public interface IStartupApplicationRunner
{
    Task<StartupRunResult> RunAsync(
        SessionHandle handle,
        SessionStartupEntry entry,
        CancellationToken cancellationToken);
}

public sealed record StartupRunResult(
    bool Succeeded,
    string? ErrorCode,
    string? Detail)
{
    public static StartupRunResult Success { get; } =
        new(true, null, null);

    public static StartupRunResult Failed(
        string errorCode,
        string? detail = null) =>
        new(false, errorCode, detail);
}
