namespace FloatDesk.Core.Models;

public static class DesktopSessionConfigurationEditor
{
    public static EnvironmentProfile AddStartupEntry(
        EnvironmentProfile environment,
        SessionStartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(entry);
        if (environment.StartupEntries.Any(existing => existing.Id == entry.Id))
        {
            throw new InvalidOperationException(
                "Desktop session startup entry IDs must be unique within an environment.");
        }

        return environment with
        {
            StartupEntries =
            [
                .. environment.StartupEntries,
                entry with { Revision = 1 }
            ]
        };
    }

    public static EnvironmentProfile UpdateStartupEntry(
        EnvironmentProfile environment,
        Guid entryId,
        Func<SessionStartupEntry, SessionStartupEntry> update)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(update);
        var entries = environment.StartupEntries.ToArray();
        var index = Array.FindIndex(entries, entry => entry.Id == entryId);
        if (index < 0)
        {
            throw new InvalidOperationException(
                "Desktop session startup entry was not found.");
        }

        var edited = update(entries[index]) ??
            throw new InvalidOperationException(
                "Desktop session startup entry edit returned null.");
        if (edited.Id != entryId)
        {
            throw new InvalidOperationException(
                "Desktop session startup entry IDs cannot be changed.");
        }

        entries[index] = edited with
        {
            Revision = DesktopSessionRevision.Next(entries[index].Revision)
        };
        return environment with { StartupEntries = entries };
    }

    public static DesktopSessionSettings AddTask(
        DesktopSessionSettings settings,
        DesktopSessionTask task)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(task);
        if (settings.Tasks.Any(existing => existing.Id == task.Id))
        {
            throw new InvalidOperationException(
                "Desktop session task IDs must be unique.");
        }

        return settings with
        {
            Tasks = [.. settings.Tasks, task with { Revision = 1 }]
        };
    }

    public static DesktopSessionSettings UpdateTask(
        DesktopSessionSettings settings,
        Guid taskId,
        Func<DesktopSessionTask, DesktopSessionTask> update)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        var tasks = settings.Tasks.ToArray();
        var index = Array.FindIndex(tasks, task => task.Id == taskId);
        if (index < 0)
        {
            throw new InvalidOperationException(
                "Desktop session task was not found.");
        }

        var edited = update(tasks[index]) ??
            throw new InvalidOperationException(
                "Desktop session task edit returned null.");
        if (edited.Id != taskId)
        {
            throw new InvalidOperationException(
                "Desktop session task IDs cannot be changed.");
        }

        tasks[index] = edited with
        {
            Revision = DesktopSessionRevision.Next(tasks[index].Revision)
        };
        return settings with { Tasks = tasks };
    }
}
