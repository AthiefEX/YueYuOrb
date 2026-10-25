using System.Text;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Notes;

public sealed class SqliteNoteRepository : INoteRepository
{
    private const int MaximumBodyBytes = 1024 * 1024;
    private const int MaximumTitleCharacters = 100;
    private readonly string _databasePath;
    private bool _initialized;

    public SqliteNoteRepository(string databasePath) =>
        _databasePath = Path.GetFullPath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=3000;
            PRAGMA user_version=1;
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY NOT NULL,
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                revision INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                deleted_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_notes_updated
                ON notes(updated_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _initialized = true;
    }

    public async Task<IReadOnlyList<NoteSummary>> SearchAsync(
        string query,
        bool recycleBin,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, revision, updated_utc, deleted_utc
            FROM notes
            WHERE (($recycle = 1 AND deleted_utc IS NOT NULL)
                OR ($recycle = 0 AND deleted_utc IS NULL))
              AND ($query = ''
                OR title LIKE '%' || $query || '%' COLLATE NOCASE
                OR body LIKE '%' || $query || '%' COLLATE NOCASE)
            ORDER BY updated_utc DESC
            LIMIT 1000;
            """;
        command.Parameters.AddWithValue("$recycle", recycleBin ? 1 : 0);
        command.Parameters.AddWithValue("$query", query.Trim());
        var found = new List<NoteSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found.Add(new NoteSummary(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                !reader.IsDBNull(4)));
        }
        return found;
    }

    public async Task<NoteDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, body, revision, created_utc, updated_utc, deleted_utc
            FROM notes WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new NoteDocument(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(6)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(6),
                    System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<OperationResult> SaveAsync(
        NoteDocument note,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        if (note.Title.Length > MaximumTitleCharacters)
            return OperationResult.Fail("NoteTitleTooLong", "标题不能超过 100 个字符。");
        if (Encoding.UTF8.GetByteCount(note.Body) > MaximumBodyBytes)
            return OperationResult.Fail("NoteTooLarge", "笔记正文不能超过 1 MiB。");

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.Transaction = (SqliteTransaction)transaction;
        countCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM notes
            WHERE deleted_utc IS NULL AND id <> $id;
            """;
        countCommand.Parameters.AddWithValue("$id", note.Id.ToString("D"));
        var otherActiveNotes = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (note.DeletedUtc is null && otherActiveNotes >= 1000)
        {
            await transaction.RollbackAsync(cancellationToken);
            return OperationResult.Fail(
                "NoteLimitReached",
                "最多保留 1000 条未删除笔记。");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO notes(id, title, body, revision, created_utc, updated_utc, deleted_utc)
            VALUES($id, $title, $body, $revision, $created, $updated, $deleted)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                body = excluded.body,
                revision = excluded.revision,
                updated_utc = excluded.updated_utc,
                deleted_utc = excluded.deleted_utc
            WHERE excluded.revision > notes.revision;
            """;
        command.Parameters.AddWithValue("$id", note.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", note.Title.Trim());
        command.Parameters.AddWithValue("$body", note.Body);
        command.Parameters.AddWithValue("$revision", note.Revision);
        command.Parameters.AddWithValue("$created", note.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated", note.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue(
            "$deleted",
            note.DeletedUtc is null ? DBNull.Value : note.DeletedUtc.Value.ToString("O"));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected > 0
            ? OperationResult.Ok()
            : OperationResult.Fail("NoteRevisionConflict", "笔记已有更新版本。");
    }

    public async Task<OperationResult> SetDeletedAsync(
        Guid id,
        bool deleted,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notes
            SET deleted_utc =
                    CASE
                        WHEN $deleted IS NOT NULL AND deleted_utc IS NOT NULL THEN deleted_utc
                        ELSE $deleted
                    END,
                revision = revision + 1,
                updated_utc = $now
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$deleted",
            deleted ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken);
        return changed > 0
            ? OperationResult.Ok()
            : OperationResult.Fail("NoteMissing", "笔记不存在。");
    }

    public async Task<OperationResult> PurgeDeletedAsync(
        DateTimeOffset cutoffUtc,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM notes
            WHERE deleted_utc IS NOT NULL AND deleted_utc < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoffUtc.ToString("O"));
        var removed = await command.ExecuteNonQueryAsync(cancellationToken);
        return OperationResult.Ok($"已清理 {removed} 条笔记。");
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("The note repository is not initialized.");
    }
}
