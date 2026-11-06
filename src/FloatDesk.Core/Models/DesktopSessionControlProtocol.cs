namespace FloatDesk.Core.Models;

public static class DesktopSessionControlProtocol
{
    public const int CurrentVersion = 1;
    public const string ListOperation = "list";
    public const string RunOperation = "run";
    public const string CancelOperation = "cancel";

    public static string GetPipeName(uint windowsSessionId) =>
        $"YueYuOrb.SessionControl.{windowsSessionId}";
}

public sealed record DesktopSessionControlRequest(
    int Version,
    string Operation,
    Guid? EnvironmentId,
    Guid? TaskId,
    Guid? RunId);

public sealed record DesktopSessionControlEnvironment(
    Guid Id,
    string Name,
    string Backend);

public sealed record DesktopSessionControlTask(
    Guid Id,
    string Name,
    Guid EnvironmentId,
    bool Enabled);

public sealed record DesktopSessionControlResponse(
    bool Success,
    string? ErrorCode,
    string? Message,
    IReadOnlyList<DesktopSessionControlEnvironment>? Environments,
    IReadOnlyList<DesktopSessionControlTask>? Tasks,
    Guid? RunId,
    string? RunOutcome,
    int? CompletedSteps,
    Guid? FailedStepId)
{
    public static DesktopSessionControlResponse Fail(
        string errorCode,
        string message) =>
        new(
            false,
            errorCode,
            message,
            Environments: null,
            Tasks: null,
            RunId: null,
            RunOutcome: null,
            CompletedSteps: null,
            FailedStepId: null);
}
