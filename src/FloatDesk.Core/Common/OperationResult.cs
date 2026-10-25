namespace FloatDesk.Core.Models;

public sealed record OperationResult(bool Success, string? ErrorCode, string? Message)
{
    public static OperationResult Ok(string? message = null) =>
        new(true, null, message);

    public static OperationResult Fail(string code, string message) =>
        new(false, code, message);
}
