using FloatDesk.Core.Contracts;
using System.Text.Json;

namespace FloatDesk.Infrastructure.DesktopSessions;

public sealed class DesktopTaskExecutionLedger :
    IDesktopTaskExecutionLedger,
    IDisposable
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 2_048;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, DateTimeOffset>? _entries;

    public DesktopTaskExecutionLedger(
        string directory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private string PrimaryPath =>
        Path.Combine(_directory, "desktop-task-executions.json");
    private string BackupPath => PrimaryPath + ".bak";

    public async Task<bool> TryBeginAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            PruneLocked(entries);
            if (!entries.TryAdd(key, _timeProvider.GetUtcNow()))
                return false;
            try
            {
                await SaveLockedAsync(entries, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                entries.Remove(key);
                throw;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Dictionary<string, DateTimeOffset>> LoadLockedAsync(
        CancellationToken cancellationToken)
    {
        if (_entries is not null)
            return _entries;
        foreach (var path in new[] { PrimaryPath, BackupPath })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var json = await File.ReadAllTextAsync(
                    path,
                    cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<LedgerDocument>(
                    json,
                    JsonOptions);
                if (document is null ||
                    document.Version != CurrentVersion ||
                    document.Entries is null)
                {
                    continue;
                }

                _entries = document.Entries
                    .Where(entry =>
                        !string.IsNullOrWhiteSpace(entry.Key) &&
                        entry.StartedAtUtc != default)
                    .GroupBy(entry => entry.Key)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Max(
                            entry => entry.StartedAtUtc),
                        StringComparer.Ordinal);
                return _entries;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException)
            {
                // Fall through to the backup snapshot.
            }
        }

        _entries = new Dictionary<string, DateTimeOffset>(
            StringComparer.Ordinal);
        return _entries;
    }

    private void PruneLocked(
        Dictionary<string, DateTimeOffset> entries)
    {
        var cutoff = _timeProvider.GetUtcNow() - Retention;
        foreach (var key in entries
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(key);
        }

        if (entries.Count <= MaximumEntries)
            return;
        foreach (var key in entries
                     .OrderByDescending(pair => pair.Value)
                     .Skip(MaximumEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(key);
        }
    }

    private async Task SaveLockedAsync(
        Dictionary<string, DateTimeOffset> entries,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var document = new LedgerDocument(
            CurrentVersion,
            entries.Select(pair => new LedgerEntry(
                pair.Key,
                pair.Value)).ToArray());
        var temporary = PrimaryPath + "." +
            Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(
                temporary,
                json,
                cancellationToken).ConfigureAwait(false);
            if (File.Exists(PrimaryPath))
            {
                File.Replace(
                    temporary,
                    PrimaryPath,
                    BackupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, PrimaryPath);
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed record LedgerDocument(
        int Version,
        LedgerEntry[]? Entries);

    private sealed record LedgerEntry(
        string Key,
        DateTimeOffset StartedAtUtc);
}
