using FloatDesk.Core.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.Core.Models;

public enum DesktopSessionBackendKind { LocalChild, HyperV }
public enum SessionStartupPolicy { EnvironmentDefault, ManagedOnly }
public enum SessionStopMode { HideView, Disconnect, LogoffOwnedSession, ShutdownGuest }
public enum SessionRuntimeStatus
{
    Stopped,
    Preparing,
    Starting,
    Ready,
    Running,
    Paused,
    Reconnecting,
    Stopping,
    Faulted
}
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
    LaunchApplication,
    WaitForWindow,
    ClickElement,
    TypeText,
    SendKeys,
    Delay,
    CaptureScreenshot,
    RunScript
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
    SessionStartupPolicy StartupPolicy,
    SessionLimits Limits,
    IReadOnlyList<SessionStartupEntry> StartupEntries)
{
    public static EnvironmentProfile CreateDefault() => new(
        Guid.Empty,
        string.Empty,
        DesktopSessionBackendKind.LocalChild,
        string.Empty,
        SessionStartupPolicy.EnvironmentDefault,
        new SessionLimits(),
        []);
}

public sealed record SessionStartupEntry(
    Guid Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int Order,
    int DelaySeconds,
    bool Enabled,
    bool WaitForExit,
    int Revision = 1);

public sealed record SessionTaskStep(
    Guid Id,
    SessionCommandKind Kind,
    IReadOnlyDictionary<string, string> Parameters,
    int TimeoutSeconds,
    int RetryCount);

public sealed record SessionTaskTrigger(
    TaskTriggerKind Kind,
    string? LocalTime,
    string? TimeZoneId);

public sealed record DesktopSessionTask(
    int FormatVersion,
    Guid Id,
    string Name,
    Guid EnvironmentId,
    IReadOnlyList<SessionTaskStep> Steps,
    SessionTaskTrigger Trigger,
    int Revision,
    bool Enabled);

public static class DesktopSessionFormat
{
    public const int CurrentTaskFormatVersion = 1;
}

public sealed record DesktopSessionSettings(
    bool Enabled,
    IReadOnlyList<EnvironmentProfile> Environments,
    IReadOnlyList<DesktopSessionTask> Tasks)
{
    public static DesktopSessionSettings CreateDefault() => new(false, [], []);
}

public sealed record SessionHandle(
    Guid EnvironmentId,
    Guid Generation,
    uint WindowsSessionId,
    Guid LoginInstanceId,
    bool PreparationCompleted);

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

public sealed record LoginIdentitySource(
    string BootId,
    string UserSid,
    string AuthenticationId,
    DateTimeOffset LoginTimeUtc);

public static class LoginInstanceIdentity
{
    public static Guid Create(LoginIdentitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.BootId) ||
            string.IsNullOrWhiteSpace(source.UserSid) ||
            string.IsNullOrWhiteSpace(source.AuthenticationId))
        {
            throw new ArgumentException(
                "Login identity fields are required.",
                nameof(source));
        }

        var input = string.Join(
            '\n',
            source.BootId,
            source.UserSid,
            source.AuthenticationId,
            source.LoginTimeUtc.ToUniversalTime().ToString("O"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash.AsSpan(0, 16));
    }
}

public sealed record SessionResourceOwnership(
    Guid EnvironmentId,
    string ResourceKind,
    string ResourceId,
    Guid OwnerRunId,
    uint? WindowsSessionId,
    int? ProcessId,
    DateTimeOffset? ProcessStartedAtUtc,
    string AuthenticationId,
    string CreatingStepId,
    DateTimeOffset CreatedAtUtc);

public static class DesktopSessionSettingsValidator
{
    public static void Validate(DesktopSessionSettings settings)
    {
        if (settings is null ||
            settings.Environments is null ||
            settings.Tasks is null)
        {
            throw new InvalidOperationException(
                "Desktop session settings are required.");
        }

        if (settings.Environments.Any(environment => environment is null) ||
            settings.Tasks.Any(task => task is null))
        {
            throw new InvalidOperationException(
                "Desktop session entries cannot be null.");
        }

        if (settings.Environments
            .GroupBy(environment => environment.Id)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "Desktop session environment IDs must be unique.");
        }

        foreach (var environment in settings.Environments)
        {
            if (!Enum.IsDefined(environment.Backend) ||
                !Enum.IsDefined(environment.StartupPolicy) ||
                environment.Limits is null ||
                environment.StartupEntries is null)
            {
                throw new InvalidOperationException(
                    "Desktop session environment configuration is invalid.");
            }

            if (environment.Limits.MaxSteps is < 1 or > 200 ||
                environment.Limits.MaxQueue is < 1 or > 20 ||
                environment.Limits.DefaultTimeoutSeconds is < 1 or > 600)
            {
                throw new InvalidOperationException(
                    "Desktop session limits are outside the supported range.");
            }

            foreach (var entry in environment.StartupEntries)
            {
                if (entry is null ||
                    entry.DelaySeconds is < 0 or > 300 ||
                    entry.Arguments is null)
                {
                    throw new InvalidOperationException(
                        "Desktop session startup entry is invalid.");
                }
            }
        }

        var environments = settings.Environments.ToDictionary(
            environment => environment.Id);
        foreach (var task in settings.Tasks)
        {
            if (task.Trigger is null ||
                !Enum.IsDefined(task.Trigger.Kind) ||
                task.Steps is null ||
                task.FormatVersion !=
                    DesktopSessionFormat.CurrentTaskFormatVersion ||
                task.Steps.Any(step =>
                    step is null ||
                    !Enum.IsDefined(step.Kind) ||
                    step.Parameters is null ||
                    step.TimeoutSeconds is < 1 or > 600 ||
                    step.RetryCount is < 0 or > 3) ||
                !environments.TryGetValue(task.EnvironmentId, out var environment) ||
                task.Steps.Count > environment.Limits.MaxSteps)
            {
                throw new InvalidOperationException(
                    "Desktop session task is invalid.");
            }

            if (task.Trigger.Kind == TaskTriggerKind.Scheduled &&
                (!TimeOnly.TryParseExact(
                     task.Trigger.LocalTime,
                     "HH:mm",
                     out _) ||
                 string.IsNullOrWhiteSpace(task.Trigger.TimeZoneId) ||
                 !TimeZoneInfo.TryFindSystemTimeZoneById(
                     task.Trigger.TimeZoneId,
                     out _)))
            {
                throw new InvalidOperationException(
                    "Scheduled desktop session task time is invalid.");
            }
        }
    }
}
