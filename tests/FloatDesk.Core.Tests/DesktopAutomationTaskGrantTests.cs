using FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.Core.Tests;

public sealed class DesktopAutomationTaskGrantTests
{
    [Fact]
    public void GrantMustMatchTaskRevisionAndEnvironment()
    {
        var task = TaskDocumentV2.Create(Guid.NewGuid(), [GuestOperation.StartApplication("C:\\Approved\\demo.exe")]) with
        { AllowedPaths = ["C:\\Approved\\"] };
        var grant = new TaskGrant(Guid.NewGuid(), task.Id, task.Revision, Guid.NewGuid(),
            [GuestOperationKind.StartApplication], ["C:\\Approved\\"], DateTimeOffset.UtcNow.AddMinutes(5), false, "manual");

        var decision = TaskPolicyEvaluator.Evaluate(task, grant, DateTimeOffset.UtcNow);

        Assert.False(decision.Allowed);
        Assert.Equal("NeedsAuthorization", decision.Code);
    }

    [Fact]
    public void MissingGrantCannotExecuteTask()
    {
        var task = TaskDocumentV2.Create(Guid.NewGuid(), [GuestOperation.SetEnvironmentVariable("X", "1")]);
        var decision = TaskPolicyEvaluator.Evaluate(task, null, DateTimeOffset.UtcNow);
        Assert.Equal("NeedsAuthorization", decision.Code);
    }
}
