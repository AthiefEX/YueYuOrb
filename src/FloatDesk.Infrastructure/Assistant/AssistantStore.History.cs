using FloatDesk.Core.Assistant;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Assistant;

public sealed partial class AssistantStore
{
    private static async Task InitializeHistorySchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_history_exclusions(session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE VIRTUAL TABLE IF NOT EXISTS assistant_chat_fts USING fts5(id UNINDEXED,body,tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS assistant_chat_insert AFTER INSERT ON messages WHEN new.role IN ('user','assistant') BEGIN
              INSERT INTO assistant_chat_fts VALUES(new.id,new.content); END;
            CREATE TRIGGER IF NOT EXISTS assistant_chat_delete AFTER DELETE ON messages BEGIN
              DELETE FROM assistant_chat_fts WHERE id=old.id; END;
            CREATE TRIGGER IF NOT EXISTS assistant_chat_update AFTER UPDATE OF content ON messages WHEN new.role IN ('user','assistant') BEGIN
              DELETE FROM assistant_chat_fts WHERE id=old.id; INSERT INTO assistant_chat_fts VALUES(new.id,new.content); END;
            CREATE TRIGGER IF NOT EXISTS assistant_chat_project_delete BEFORE DELETE ON projects BEGIN
              INSERT OR IGNORE INTO chat_history_exclusions SELECT session_id FROM session_projects WHERE project_id=old.id; END;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        // One-time backfill rather than rescanning all historical text on every launch.
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='assistant_chat_index_version'";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            command.CommandText = "INSERT INTO assistant_chat_fts SELECT id,content FROM messages WHERE role IN ('user','assistant'); CREATE TABLE assistant_chat_index_version(version INTEGER NOT NULL); INSERT INTO assistant_chat_index_version VALUES(1);";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AssistantRecall>> RecallHistoryAsync(string query, Guid? project, int budget, Guid? excludeMessage, CancellationToken token)
        => Task.Run(() => RecallHistoryCoreAsync(query, project, budget, excludeMessage, token), token);

    private async Task<IReadOnlyList<AssistantRecall>> RecallHistoryCoreAsync(string query, Guid? project, int budget, Guid? excludeMessage, CancellationToken token)
    {
        if (query.Length > 32000 || budget is < 1 or > 200000) throw new ArgumentException("历史检索查询或预算无效。");
        var terms = RecallTerms(query); if (terms.Count == 0) return [];
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        var match = string.Join(" OR ", terms.Where(term => term.Length >= 3).Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
        var sql = match.Length > 0 ? """
            SELECT m.id,m.role,s.title,m.created,snippet(assistant_chat_fts,1,'','',' … ',64),p.project_id
            FROM assistant_chat_fts JOIN messages m ON m.id=assistant_chat_fts.id JOIN sessions s ON s.id=m.session_id
            LEFT JOIN session_projects p ON p.session_id=s.id
            WHERE assistant_chat_fts MATCH $query AND (p.project_id IS NULL OR p.project_id=$project)
              AND ($exclude IS NULL OR m.id<>$exclude) AND NOT EXISTS(SELECT 1 FROM chat_history_exclusions e WHERE e.session_id=s.id)
            ORDER BY bm25(assistant_chat_fts),m.created DESC LIMIT 24
            """ : """
            SELECT m.id,m.role,s.title,m.created,substr(m.content,max(1,instr(lower(m.content),lower($query))-160),1400),p.project_id
            FROM messages m JOIN sessions s ON s.id=m.session_id LEFT JOIN session_projects p ON p.session_id=s.id
            WHERE m.role IN ('user','assistant') AND instr(lower(m.content),lower($query))>0 AND (p.project_id IS NULL OR p.project_id=$project)
              AND ($exclude IS NULL OR m.id<>$exclude) AND NOT EXISTS(SELECT 1 FROM chat_history_exclusions e WHERE e.session_id=s.id)
            ORDER BY m.created DESC LIMIT 24
            """;
        using var command = Command(connection, sql, ("$query", match.Length > 0 ? match : query.Trim()), ("$project", project?.ToString()), ("$exclude", excludeMessage?.ToString()));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var hits = new List<AssistantRecall>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested(); var text = reader.GetString(4); if (text.Length > 1400) text = text[..1400];
            if (string.IsNullOrWhiteSpace(text) || text.Length + 180 > budget) continue;
            hits.Add(new(Guid.Parse(reader.GetString(0)), "chat", text, reader.GetString(2), reader.GetString(3) + " · " + reader.GetString(1), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), "ask", 1, 1d / (61 + hits.Count)));
            budget -= text.Length + 180; if (hits.Count >= 4) break;
        }
        return hits;
    }

    public async Task<AssistantRecall?> ReadHistoryCitationAsync(Guid id, Guid? project, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, """
            SELECT m.content,m.role,m.created,s.title,p.project_id FROM messages m JOIN sessions s ON s.id=m.session_id LEFT JOIN session_projects p ON p.session_id=s.id
            WHERE m.id=$id AND m.role IN ('user','assistant') AND (p.project_id IS NULL OR p.project_id=$project)
              AND NOT EXISTS(SELECT 1 FROM chat_history_exclusions e WHERE e.session_id=s.id)
            """, ("$id", id.ToString()), ("$project", project?.ToString()));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new(id, "chat", reader.GetString(0), reader.GetString(3), reader.GetString(2) + " · " + reader.GetString(1), reader.IsDBNull(4) ? null : Guid.Parse(reader.GetString(4)), "ask", 1, 0) : null;
    }
}
