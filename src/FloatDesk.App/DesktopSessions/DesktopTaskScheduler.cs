using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.App.DesktopSessions;

public interface IDesktopTaskScheduleContext
{
    bool IsEnvironmentReady(Guid environmentId);

    Guid? GetLoginInstanceId(Guid environmentId);

    Task<DesktopTaskRunResult> RunAsync(
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken);
}

public sealed class DesktopTaskScheduler
{
    private readonly IDesktopTaskExecutionLedger _ledger;
    private readonly IDesktopTaskScheduleContext _context;

    public DesktopTaskScheduler(
        IDesktopTaskExecutionLedger ledger,
        IDesktopTaskScheduleContext context)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _context = context ??
            throw new ArgumentNullException(nameof(context));
    }

    public async Task<int> ProcessScheduledAsync(
        IReadOnlyList<DesktopSessionTask> tasks,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        if (toUtc <= fromUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(toUtc),
                "The schedule window must end after it starts.");
        }

        var started = 0;
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (task is null ||
                !task.Enabled ||
                task.Trigger.Kind != TaskTriggerKind.Scheduled)
            {
                continue;
            }

            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(
                task.Trigger.TimeZoneId!);
            var time = TimeOnly.ParseExact(
                task.Trigger.LocalTime!,
                "HH:mm",
                CultureInfo.InvariantCulture);
            foreach (var occurrence in GetOccurrences(
                         task,
                         timeZone,
                         time,
                         fromUtc,
                         toUtc))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await _ledger.TryBeginAsync(
                        occurrence.Key,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // A missed occurrence is consumed even while unavailable so
                // reconnecting later cannot replay it.
                if (!_context.IsEnvironmentReady(
                        task.EnvironmentId))
                {
                    continue;
                }

                await _context.RunAsync(
                    task,
                    CreateRunId(occurrence.Key),
                    cancellationToken).ConfigureAwait(false);
                started++;
            }
        }

        return started;
    }

    public async Task<bool> ProcessEnvironmentReadyAsync(
        DesktopSessionTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!task.Enabled ||
            task.Trigger.Kind != TaskTriggerKind.EnvironmentReady ||
            !_context.IsEnvironmentReady(task.EnvironmentId) ||
            _context.GetLoginInstanceId(task.EnvironmentId)
                is not { } loginInstanceId ||
            loginInstanceId == Guid.Empty)
        {
            return false;
        }

        var key = string.Join(
            ':',
            "environment-ready",
            task.EnvironmentId.ToString("N"),
            loginInstanceId.ToString("N"),
            task.Id.ToString("N"),
            task.Revision.ToString(CultureInfo.InvariantCulture));
        if (!await _ledger.TryBeginAsync(
                key,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await _context.RunAsync(
            task,
            CreateRunId(key),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static IEnumerable<ScheduleOccurrence> GetOccurrences(
        DesktopSessionTask task,
        TimeZoneInfo timeZone,
        TimeOnly time,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var firstDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(fromUtc, timeZone).DateTime)
            .AddDays(-1);
        var lastDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(toUtc, timeZone).DateTime)
            .AddDays(1);
        for (var date = firstDate;
             date <= lastDate;
             date = date.AddDays(1))
        {
            var local = DateTime.SpecifyKind(
                date.ToDateTime(time),
                DateTimeKind.Unspecified);
            if (timeZone.IsInvalidTime(local))
                continue;
            var offsets = timeZone.IsAmbiguousTime(local)
                ? timeZone.GetAmbiguousTimeOffsets(local)
                : [timeZone.GetUtcOffset(local)];
            var scheduledUtc = offsets
                .Select(offset => new DateTimeOffset(local, offset)
                    .ToUniversalTime())
                .Min();
            if (scheduledUtc < fromUtc || scheduledUtc >= toUtc)
                continue;

            var localKey = local.ToString(
                "yyyy-MM-dd'T'HH:mm",
                CultureInfo.InvariantCulture);
            yield return new ScheduleOccurrence(
                scheduledUtc,
                string.Join(
                    ':',
                    "scheduled",
                    task.EnvironmentId.ToString("N"),
                    task.Id.ToString("N"),
                    task.Revision.ToString(
                        CultureInfo.InvariantCulture),
                    timeZone.Id,
                    localKey));
        }
    }

    private static Guid CreateRunId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record ScheduleOccurrence(
        DateTimeOffset ScheduledUtc,
        string Key);
}
