using FloatDesk.Core.Contracts;

namespace FloatDesk.Core.Models;

public static class DesktopSessionRevision
{
    public static int Next(int currentRevision) =>
        currentRevision >= int.MaxValue ? 1 : Math.Max(1, currentRevision) + 1;
}

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

public static class DesktopSessionPreparationGuard
{
    public static PreparationResult RequireSuccessful(
        PreparationResult preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.Succeeded)
        {
            throw new InvalidOperationException(
                "A successful preparation is required before start.");
        }

        return preparation;
    }
}
