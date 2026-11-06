using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatDesk.Infrastructure.DesktopSessions;

public sealed class SessionExecutionLedger :
    IStartupExecutionLedger,
    IDisposable
{
    private const int CurrentVersion = 1;
    private const int MaximumEntries = 512;
    private static readonly TimeSpan Retention =
        TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<StartupExecutionKey, DateTimeOffset>? _entries;

    public SessionExecutionLedger(
        string directory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private string PrimaryPath =>
        Path.Combine(_directory, "session-executions.json");
    private string BackupPath => PrimaryPath + ".bak";

    public async Task<bool> TryBeginAsync(
        StartupExecutionKey key,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadLockedAsync(cancellationToken)
                .ConfigureAwait(false);
            PruneLocked(entries);
            if (entries.ContainsKey(key))
                return false;

            entries.Add(key, _timeProvider.GetUtcNow());
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

    private async Task<Dictionary<StartupExecutionKey, DateTimeOffset>>
        LoadLockedAsync(CancellationToken cancellationToken)
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
                    .Where(entry => entry.StartedAtUtc != default)
                    .GroupBy(entry => entry.Key)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Max(entry => entry.StartedAtUtc));
                return _entries;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException)
            {
                // Try the previous valid snapshot next.
            }
        }

        _entries = [];
        return _entries;
    }

    private void PruneLocked(
        Dictionary<StartupExecutionKey, DateTimeOffset> entries)
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
        Dictionary<StartupExecutionKey, DateTimeOffset> entries,
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
        StartupExecutionKey Key,
        DateTimeOffset StartedAtUtc);
}
