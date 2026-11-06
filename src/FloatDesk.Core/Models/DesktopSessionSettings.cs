using FloatDesk.Core.Contracts;

namespace FloatDesk.Core.Models;

public enum DesktopSessionBackendKind { LocalChild, HyperV }
public enum SessionStartupPolicy { EnvironmentDefault, ManagedOnly }
public enum SessionStopMode { HideView, Disconnect, LogoffOwnedSession, ShutdownGuest }
public enum SessionBackendEventKind
{
    Connected,
    Disconnected,
    Locked,
    Unlocked,
    ResolutionChanged,
    AgentExited,
    SessionEnded,
    CapabilitiesChanged
}
public enum SessionCommandKind
{
    Click,
    TypeText,
    SendKeys,
    Capture,
    Launch,
    Script
}
public enum TaskTriggerKind { Manual, EnvironmentReady, Scheduled }

public sealed record SessionLimits(
    int MaxSteps = 200,
    int MaxQueue = 20,
    int DefaultTimeoutSeconds = 30);

public sealed record EnvironmentProfile(
    Guid Id,
    string Name,
    DesktopSessionBackendKind Backend,
    string BackendProfileId,
    SessionLimits Limits);

public sealed record SessionStartupEntry(
    Guid Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int Order,
    int DelaySeconds,
    bool Enabled);

public sealed record SessionTaskStep(
    Guid Id,
    SessionCommandKind Kind,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record SessionTaskTrigger(TaskTriggerKind Kind, string? LocalTime);

public sealed record DesktopSessionTask(
    Guid Id,
    string Name,
    Guid EnvironmentId,
    IReadOnlyList<SessionTaskStep> Steps,
    SessionTaskTrigger Trigger,
    bool Enabled);

public sealed record DesktopSessionSettings(
    bool Enabled,
    IReadOnlyList<EnvironmentProfile> Environments,
    IReadOnlyList<SessionStartupEntry> StartupEntries,
    IReadOnlyList<DesktopSessionTask> Tasks)
{
    public static DesktopSessionSettings CreateDefault() => new(false, [], [], []);
}

public sealed record SessionHandle(
    Guid EnvironmentId,
    Guid Generation,
    uint WindowsSessionId,
    Guid LoginInstanceId);

public sealed record SessionCapabilities(
    bool CanRunHidden,
    bool CanSuppressStartupBeforeLaunch,
    IReadOnlyList<string> SupportedStartupSources,
    IReadOnlyList<string> UnsupportedStartupSources);

public sealed record SessionBackendEvent(
    Guid EnvironmentId,
    Guid Generation,
    DateTimeOffset ObservedAt,
    SessionBackendEventKind Kind,
    Guid LoginInstanceId,
    int? Width,
    int? Height);

public sealed record SessionCommand(
    Guid CommandId,
    SessionHandle Handle,
    SessionCommandKind Kind,
    IReadOnlyDictionary<string, string> Parameters,
    DateTimeOffset DeadlineUtc);

public sealed record CommandResult(
    Guid CommandId,
    bool Succeeded,
    bool OutcomeUnknown,
    string? ErrorCode);

public static class DesktopSessionSettingsValidator
{
    public static void Validate(DesktopSessionSettings settings)
    {
        if (settings.Environments
            .GroupBy(environment => environment.Id)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "Desktop session environment IDs must be unique.");
        }

        if (settings.StartupEntries.Any(entry =>
                entry.DelaySeconds is < 0 or > 300))
        {
            throw new InvalidOperationException(
                "Desktop session startup delay must be between 0 and 300 seconds.");
        }

        foreach (var environment in settings.Environments)
        {
            if (environment.Limits.MaxSteps is < 1 or > 200 ||
                environment.Limits.MaxQueue is < 1 or > 20 ||
                environment.Limits.DefaultTimeoutSeconds is < 1 or > 600)
            {
                throw new InvalidOperationException(
                    "Desktop session limits are outside the supported range.");
            }
        }
    }
}
