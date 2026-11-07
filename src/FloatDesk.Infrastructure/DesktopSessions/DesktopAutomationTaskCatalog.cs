using FloatDesk.Core.Models.DesktopAutomation;
using System.Text.Json;

namespace FloatDesk.Infrastructure.DesktopSessions;

public sealed class DesktopAutomationTaskCatalog : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public DesktopAutomationTaskCatalog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "automation-tasks-v2.json");
    }

    public async Task<TaskDocumentV2> CreateAsync(TaskDocumentV2 task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        EnsureValid(task);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (all.Any(item => item.Id == task.Id)) throw new InvalidOperationException("TaskAlreadyExists");
            all.Add(task);
            await SaveAsync(all, cancellationToken).ConfigureAwait(false);
            return task;
        }
        finally { _gate.Release(); }
    }

    public async Task<TaskDocumentV2> UpdateAsync(Guid id, int expectedRevision, TaskDocumentV2 replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        EnsureValid(replacement);
        if (replacement.Id != id) throw new InvalidOperationException("TaskIdentityMismatch");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var index = all.FindIndex(item => item.Id == id);
            if (index < 0) throw new KeyNotFoundException("TaskNotFound");
            if (all[index].Revision != expectedRevision) throw new InvalidOperationException("TaskRevisionConflict");
            var updated = replacement with { Revision = checked(expectedRevision + 1) };
            all[index] = updated;
            await SaveAsync(all, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TaskDocumentV2>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<List<TaskDocumentV2>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<TaskDocumentV2>>(json, Options) ?? [];
    }

    private async Task SaveAsync(List<TaskDocumentV2> tasks, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(tasks, Options), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void EnsureValid(TaskDocumentV2 task)
    {
        var validation = TaskDocumentV2Validator.Validate(task);
        if (!validation.IsValid) throw new InvalidOperationException(validation.Errors[0].Code);
    }

    public void Dispose() => _gate.Dispose();
}
