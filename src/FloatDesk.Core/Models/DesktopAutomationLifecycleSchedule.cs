namespace FloatDesk.Core.Models.DesktopAutomation;

public enum LifecycleScheduleKind { Once, Daily, Weekly }
public sealed record LifecycleSchedule(Guid Id, Guid EnvironmentId, Guid? TaskId, LifecycleScheduleKind Kind, DateTimeOffset FirstRun, TimeSpan? Interval, string TimeZoneId, bool ShutdownAfterTask, bool Enabled, int Revision = 1);

public static class LifecycleScheduleValidator
{
    public static IReadOnlyList<string> Validate(LifecycleSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        var errors = new List<string>();
        if (schedule.Id == Guid.Empty || schedule.EnvironmentId == Guid.Empty) errors.Add("schedule identity is required");
        if (!Enum.IsDefined(schedule.Kind)) errors.Add("schedule kind is invalid");
        if (schedule.Kind != LifecycleScheduleKind.Once && schedule.Interval is null) errors.Add("repeat interval is required");
        if (schedule.Revision < 1 || string.IsNullOrWhiteSpace(schedule.TimeZoneId)) errors.Add("schedule metadata is invalid");
        return errors;
    }
}
