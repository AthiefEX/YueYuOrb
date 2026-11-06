namespace FloatDesk.Core.Models;

public readonly record struct StartupExecutionKey(
    Guid EnvironmentId,
    Guid LoginInstanceId,
    Guid StartupEntryId,
    int EntryRevision);

public readonly record struct ScheduledTaskExecutionKey(
    Guid TaskId,
    int TaskRevision,
    string TimeZoneId,
    DateOnly LocalDate,
    TimeOnly LocalTime);
