namespace FloatDesk.Core.Models.DesktopAutomation;

public sealed record TaskGrant(
    Guid GrantId,
    Guid TaskId,
    int TaskRevision,
    Guid EnvironmentId,
    IReadOnlySet<GuestOperationKind> Operations,
    IReadOnlyList<string> AllowedPaths,
    DateTimeOffset ExpiresAtUtc,
    bool AllowShutdown,
    string Source)
{
    public bool Covers(TaskDocumentV2 task, DateTimeOffset now)
    {
        if (task.Id != TaskId || task.Revision != TaskRevision || task.EnvironmentId != EnvironmentId || now >= ExpiresAtUtc)
            return false;
        if (task.ShutdownAfterSuccess && !AllowShutdown)
            return false;
        return task.Operations.All(operation => Operations.Contains(operation.Kind) &&
            (!operation.Parameters.TryGetValue("path", out var path) || AllowedPaths.Any(root => IsUnderRoot(path, root))));
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (!Path.IsPathRooted(path) || !Path.IsPathRooted(root)) return false;
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var allowed = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(allowed, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TaskPolicyDecision(bool Allowed, string Code, string Message)
{
    public static TaskPolicyDecision Allow { get; } = new(true, "Allowed", "Task is covered by the grant.");
}

public static class TaskPolicyEvaluator
{
    public static TaskPolicyDecision Evaluate(TaskDocumentV2 task, TaskGrant? grant, DateTimeOffset now)
    {
        var validation = TaskDocumentV2Validator.Validate(task);
        if (!validation.IsValid)
            return new(false, "TaskInvalid", validation.Errors[0].Message);
        if (grant is null)
            return new(false, "NeedsAuthorization", "A scoped task grant is required.");
        return grant.Covers(task, now)
            ? TaskPolicyDecision.Allow
            : new(false, "NeedsAuthorization", "The task changed or exceeds the granted scope.");
    }
}
