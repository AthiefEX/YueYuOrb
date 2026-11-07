namespace FloatDesk.Core.Models.DesktopAutomation;

public enum TaskExecutionMode { Fixed, Adaptive }
public enum GuestOperationKind { StartApplication, StopApplication, RestartApplication, SetEnvironmentVariable, CopyFile, SetServiceState, SetRegistryValue, SetNetworkProfile, RestartGuest, ShutdownGuest }

public sealed record GuestOperation(GuestOperationKind Kind, IReadOnlyDictionary<string, string> Parameters)
{
    public static GuestOperation StartApplication(string path) => new(GuestOperationKind.StartApplication, new Dictionary<string, string> { ["path"] = path });
    public static GuestOperation SetEnvironmentVariable(string name, string value) => new(GuestOperationKind.SetEnvironmentVariable, new Dictionary<string, string> { ["name"] = name, ["value"] = value });
}

public sealed record TaskDocumentV2(
    Guid Id, int Revision, Guid EnvironmentId, TaskExecutionMode Mode,
    IReadOnlyList<GuestOperation> Operations, IReadOnlyList<string> AllowedCapabilities,
    IReadOnlyList<string> AllowedPaths, string? CredentialRef, bool ShutdownAfterSuccess,
    int MaxDurationSeconds)
{
    public static TaskDocumentV2 Create(Guid environmentId, IReadOnlyList<GuestOperation> operations) => new(Guid.NewGuid(), 1, environmentId, TaskExecutionMode.Fixed, operations, [], [], null, false, 1800);
}

public sealed record TaskValidationError(string Code, string Message);
public sealed record TaskValidationResult(bool IsValid, IReadOnlyList<TaskValidationError> Errors)
{
    public static TaskValidationResult Valid { get; } = new(true, []);
}

public static class TaskDocumentV2Validator
{
    public static TaskValidationResult Validate(TaskDocumentV2 task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var errors = new List<TaskValidationError>();
        if (task.Id == Guid.Empty || task.EnvironmentId == Guid.Empty) errors.Add(new("IdentityMissing", "task and environment IDs are required"));
        if (task.Revision < 1 || task.MaxDurationSeconds is < 1 or > 86400) errors.Add(new("LimitsInvalid", "task revision or duration is invalid"));
        if (task.Operations is null || task.Operations.Count > 200) errors.Add(new("OperationLimit", "operation count is outside the supported range"));
        foreach (var operation in task.Operations ?? [])
        {
            if (!Enum.IsDefined(operation.Kind) || operation.Parameters is null)
            {
                errors.Add(new("OperationInvalid", "operation is not recognized"));
                continue;
            }
            if (operation.Parameters.TryGetValue("path", out var path) && !task.AllowedPaths.Any(root => IsUnderRoot(path, root))) errors.Add(new("PathOutsideGrant", "operation path is outside the granted roots"));
        }
        return errors.Count == 0 ? TaskValidationResult.Valid : new(false, errors);
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (!Path.IsPathRooted(path) || !Path.IsPathRooted(root)) return false;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
