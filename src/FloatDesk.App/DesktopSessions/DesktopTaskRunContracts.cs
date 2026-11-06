using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public enum DesktopTaskRunOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    UnknownOutcome
}

public enum DesktopTaskRunPhase
{
    StepStarted,
    StepSucceeded,
    StepFailed,
    StepCancelled
}

public sealed record DesktopTaskRunResult(
    Guid RunId,
    Guid TaskId,
    DesktopTaskRunOutcome Outcome,
    int CompletedSteps,
    Guid? FailedStepId,
    string? ErrorCode);

public sealed record DesktopTaskRunEvent(
    Guid RunId,
    Guid StepId,
    int StepIndex,
    SessionCommandKind Kind,
    DesktopTaskRunPhase Phase);

public interface IDesktopTaskRunObserver
{
    void Report(DesktopTaskRunEvent value);
}

public sealed record DesktopSessionResolution(
    int Width,
    int Height);

public interface IDesktopTaskResolutionProvider
{
    Task<DesktopSessionResolution> GetResolutionAsync(
        SessionHandle handle,
        CancellationToken cancellationToken);
}
