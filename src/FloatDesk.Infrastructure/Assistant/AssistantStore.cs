using System.Globalization;
using System.Text.Json;
using FloatDesk.Core.Assistant;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Assistant;

public sealed partial class AssistantStore(string databasePath) : IAssistantStore, IAssistantKnowledgeStore, IDisposable
{
    private readonly string _path = Path.GetFullPath(databasePath);
    private readonly SemaphoreSlim _initialize = new(1, 1);
    private bool _initialized;
    public void Dispose() => _initialize.Dispose();

    public async Task InitializeAsync(CancellationToken token)
    {
        await _initialize.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var connection = await OpenRawAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, title TEXT NOT NULL, created TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS messages(seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL,
                    session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE, role TEXT NOT NULL,
                    content TEXT NOT NULL, created TEXT NOT NULL, calls TEXT NOT NULL, call_id TEXT NULL);
                CREATE INDEX IF NOT EXISTS ix_assistant_messages ON messages(session_id,seq);
                CREATE TABLE IF NOT EXISTS memories(id TEXT PRIMARY KEY, content TEXT NOT NULL,
                    source_id TEXT REFERENCES messages(id) ON DELETE CASCADE, revision INTEGER NOT NULL, enabled INTEGER NOT NULL, updated TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS memory_revisions(memory_id TEXT REFERENCES memories(id) ON DELETE CASCADE,
                    revision INTEGER NOT NULL, content TEXT NOT NULL, updated TEXT NOT NULL, PRIMARY KEY(memory_id,revision));
                CREATE TABLE IF NOT EXISTS memory_suppressions(source_id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS assets(id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL,
                    content TEXT NOT NULL, source TEXT NOT NULL, hash TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await InitializeKnowledgeSchemaAsync(connection, token).ConfigureAwait(false);
            await Task.Run(() => InitializeHistorySchemaAsync(connection, token), token).ConfigureAwait(false); _initialized = true;
        }
        finally { _initialize.Release(); }
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false, ForeignKeys = true, DefaultTimeout = 5 }.ToString());
        try { await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    { await InitializeAsync(token).ConfigureAwait(false); return await OpenRawAsync(token).ConfigureAwait(false); }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ReadStamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static void Content(string value, int maximum)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("助手内容为空或超过长度限制。"); }

    public async Task<AssistantSession> CreateSessionAsync(string title, CancellationToken token)
    {
        Content(title, 120); var session = new AssistantSession(Guid.NewGuid(), title, DateTimeOffset.UtcNow);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, "INSERT INTO sessions VALUES($id,$title,$created)", ("$id", session.Id.ToString()), ("$title", title), ("$created", Stamp(session.CreatedAt)));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); return session;
    }

    public async Task<IReadOnlyList<AssistantSession>> ListSessionsAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, "SELECT id,title,created FROM sessions ORDER BY created DESC LIMIT 500");
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantSession>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ReadStamp(reader.GetString(2))));
        return result;
    }

    public async Task AppendMessageAsync(AssistantMessage message, CancellationToken token)
    {
        if (message.Id == Guid.Empty || message.SessionId == Guid.Empty || message.Role is not ("user" or "assistant" or "tool" or "system") || message.Content is null || message.Content.Length > 200000 || message.ToolCalls.Count > 16)
            throw new ArgumentException("助手消息无效。", nameof(message));
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, "INSERT INTO messages(id,session_id,role,content,created,calls,call_id) VALUES($id,$session,$role,$content,$created,$calls,$callId)",
            ("$id", message.Id.ToString()), ("$session", message.SessionId.ToString()), ("$role", message.Role), ("$content", message.Content),
            ("$created", Stamp(message.CreatedAt)), ("$calls", JsonSerializer.Serialize(message.ToolCalls)), ("$callId", message.ToolCallId));
        using var transaction = connection.BeginTransaction(); command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (message.References.Count > 0)
        {
            if (message.References.Count > 20) throw new ArgumentException("引用数量过多。");
            using var references = Command(connection, "INSERT INTO assistant_message_refs VALUES($id,$payload)", ("$id", message.Id.ToString()), ("$payload", JsonSerializer.Serialize(message.References))); references.Transaction = transaction; await references.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (message.Role == "user" && message.ExtractionFingerprint is { Length: 64 } fingerprint)
        {
            using var job = Command(connection, "INSERT OR IGNORE INTO memory_jobs(id,source_id,fingerprint,state,attempts,error,project_id) SELECT $job,$source,$fingerprint,'queued',0,NULL,p.project_id FROM sessions s LEFT JOIN session_projects p ON p.session_id=s.id WHERE s.id=$session",
                ("$job", Guid.NewGuid().ToString()), ("$source", message.Id.ToString()), ("$fingerprint", fingerprint), ("$session", message.SessionId.ToString())); job.Transaction = transaction; await job.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssistantMessage>> ReadMessagesAsync(Guid sessionId, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, "SELECT m.id,m.role,m.content,m.created,m.calls,m.call_id,r.payload FROM (SELECT * FROM messages WHERE session_id=$id ORDER BY seq DESC LIMIT 1000) m LEFT JOIN assistant_message_refs r ON r.message_id=m.id ORDER BY m.seq", ("$id", sessionId.ToString()));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantMessage>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), sessionId, reader.GetString(1), reader.GetString(2), ReadStamp(reader.GetString(3)))
        { ToolCalls = JsonSerializer.Deserialize<AssistantToolCall[]>(reader.GetString(4)) ?? [], ToolCallId = reader.IsDBNull(5) ? null : reader.GetString(5), References = reader.IsDBNull(6) ? [] : JsonSerializer.Deserialize<AssistantCitation[]>(reader.GetString(6)) ?? [] });
        return result;
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        using var command = Command(connection, "DELETE FROM sessions WHERE id=$id", ("$id", sessionId.ToString())); command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke();
    }

    public async Task<AssistantMemory> SaveMemoryAsync(AssistantMemory memory, CancellationToken token)
    {
        Content(memory.Content, 4000);
        if (memory.Id == Guid.Empty || memory.Revision < 1) throw new ArgumentException("记忆标识或修订无效。", nameof(memory));
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction();
        if (memory.SourceMessageId is { } source)
        {
            using var suppressed = Command(connection, "SELECT COUNT(*) FROM memory_suppressions WHERE source_id=$id", ("$id", source.ToString())); suppressed.Transaction = transaction;
            if (Convert.ToInt32(await suppressed.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException("该来源已撤回记忆授权。");
        }
        using var lookup = Command(connection, "SELECT revision FROM memories WHERE id=$id", ("$id", memory.Id.ToString())); lookup.Transaction = transaction;
        var existing = await lookup.ExecuteScalarAsync(token).ConfigureAwait(false);
        var revision = existing is null ? 1 : checked(Convert.ToInt32(existing, CultureInfo.InvariantCulture) + 1);
        if ((existing is null && memory.Revision != 1) || (existing is not null && revision - 1 != memory.Revision)) throw new InvalidOperationException("记忆已变化，请重新读取后合并。");
        var saved = memory with { Revision = revision, UpdatedAt = DateTimeOffset.UtcNow };
        using var command = Command(connection, """
            INSERT INTO memories VALUES($id,$content,$source,$revision,$enabled,$updated)
            ON CONFLICT(id) DO UPDATE SET content=excluded.content,source_id=excluded.source_id,revision=excluded.revision,enabled=excluded.enabled,updated=excluded.updated;
            INSERT INTO memory_revisions VALUES($id,$revision,$content,$updated);
            """, ("$id", saved.Id.ToString()), ("$content", saved.Content), ("$source", saved.SourceMessageId?.ToString()), ("$revision", saved.Revision), ("$enabled", saved.Enabled ? 1 : 0), ("$updated", Stamp(saved.UpdatedAt)));
        command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await SaveMemoryInfoAsync(connection, transaction, saved, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke(); return saved;
    }

    public async Task<IReadOnlyList<AssistantMemory>> ListMemoriesAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,content,source_id,revision,enabled,updated FROM memories ORDER BY updated DESC LIMIT 500");
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantMemory>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)), reader.GetInt32(3), reader.GetInt32(4) != 0, ReadStamp(reader.GetString(5))));
        reader.Close();
        for (var index = 0; index < result.Count; index++) result[index] = await ReadMemoryInfoAsync(connection, result[index], token).ConfigureAwait(false);
        return result;
    }

    public async Task DeleteMemoryAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction();
        await SuppressMemoryHashAsync(connection, transaction, id, token).ConfigureAwait(false);
        using var command = Command(connection, "INSERT OR IGNORE INTO memory_suppressions SELECT source_id FROM memories WHERE id=$id AND source_id IS NOT NULL; DELETE FROM memories WHERE id=$id", ("$id", id.ToString()));
        command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke();
    }

    public async Task SaveAssetAsync(AssistantAsset asset, CancellationToken token)
    {
        Content(asset.Name, 100); Content(asset.Text, 64000);
        if (asset.Id == Guid.Empty || asset.Kind is not ("skill" or "role") || asset.Sha256.Length != 64 || asset.SourcePath.Length > 4096) throw new ArgumentException("素材格式无效。", nameof(asset));
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, "INSERT INTO assets VALUES($id,$name,$kind,$content,$source,$hash) ON CONFLICT(id) DO UPDATE SET name=excluded.name,kind=excluded.kind,content=excluded.content,source=excluded.source,hash=excluded.hash",
            ("$id", asset.Id.ToString()), ("$name", asset.Name), ("$kind", asset.Kind), ("$content", asset.Text), ("$source", asset.SourcePath), ("$hash", asset.Sha256));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task SaveAssetVersionedAsync(AssistantAsset asset, string? expectedHash, CancellationToken token)
    {
        Content(asset.Name, 100); Content(asset.Text, 64000);
        if (asset.Id == Guid.Empty || asset.Kind is not ("skill" or "role") || asset.Sha256.Length != 64 || asset.SourcePath.Length > 4096) throw new ArgumentException("素材格式无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction();
        using (var check = Command(connection, "SELECT hash FROM assets WHERE id=$id", ("$id", asset.Id.ToString())))
        { check.Transaction = transaction; var current = await check.ExecuteScalarAsync(token).ConfigureAwait(false) as string; if (current != expectedHash) throw new InvalidOperationException("素材已变化或删除，请重新读取后编辑。"); }
        using (var count = Command(connection, "SELECT COUNT(*) FROM assets"))
        { count.Transaction = transaction; if (expectedHash is null && Convert.ToInt32(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) >= 500) throw new InvalidOperationException("素材数量已达500上限。"); }
        using var command = Command(connection, "INSERT INTO assets VALUES($id,$name,$kind,$content,$source,$hash) ON CONFLICT(id) DO UPDATE SET name=excluded.name,kind=excluded.kind,content=excluded.content,source=excluded.source,hash=excluded.hash",
            ("$id", asset.Id.ToString()), ("$name", asset.Name), ("$kind", asset.Kind), ("$content", asset.Text), ("$source", asset.SourcePath), ("$hash", asset.Sha256)); command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssistantAsset>> ListAssetsAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,name,kind,content,source,hash FROM assets ORDER BY name LIMIT 500");
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantAsset>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public async Task DeleteAssetAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "DELETE FROM assets WHERE id=$id", ("$id", id.ToString()));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
