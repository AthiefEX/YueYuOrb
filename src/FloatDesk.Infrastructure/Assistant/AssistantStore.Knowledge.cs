using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloatDesk.Core.Assistant;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Assistant;

public sealed partial class AssistantStore
{
    private static async Task InitializeKnowledgeSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY,name TEXT NOT NULL,created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS session_projects(session_id TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE, project_id TEXT REFERENCES projects(id) ON DELETE SET NULL);
            CREATE TABLE IF NOT EXISTS assistant_message_refs(message_id TEXT PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memory_info(memory_id TEXT PRIMARY KEY REFERENCES memories(id) ON DELETE CASCADE,project_id TEXT REFERENCES projects(id) ON DELETE CASCADE,
                kind TEXT NOT NULL,memory_key TEXT NOT NULL,state TEXT NOT NULL,confidence REAL NOT NULL,importance REAL NOT NULL,expires TEXT,cloud_policy TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS documents(id TEXT PRIMARY KEY,project_id TEXT REFERENCES projects(id) ON DELETE CASCADE,name TEXT NOT NULL,source TEXT NOT NULL,sha TEXT NOT NULL,enabled INTEGER NOT NULL,cloud_policy TEXT NOT NULL,revision INTEGER NOT NULL,created TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_document_hash ON documents(IFNULL(project_id,''),sha);
            CREATE TABLE IF NOT EXISTS document_chunks(id TEXT PRIMARY KEY,document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,ordinal INTEGER NOT NULL,locator TEXT NOT NULL,content TEXT NOT NULL, UNIQUE(document_id,ordinal));
            CREATE VIRTUAL TABLE IF NOT EXISTS assistant_fts USING fts5(id UNINDEXED,kind UNINDEXED,body,tokenize='trigram');
            CREATE TABLE IF NOT EXISTS memory_candidates(id TEXT PRIMARY KEY,source_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,project_id TEXT REFERENCES projects(id) ON DELETE CASCADE,
                payload TEXT NOT NULL,hash TEXT NOT NULL UNIQUE,approved_memory TEXT REFERENCES memories(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS candidate_sources(candidate_id TEXT NOT NULL REFERENCES memory_candidates(id) ON DELETE CASCADE,source_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,PRIMARY KEY(candidate_id,source_id));
            CREATE TABLE IF NOT EXISTS candidate_suppressions(hash TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS assistant_vectors(id TEXT NOT NULL,kind TEXT NOT NULL,fingerprint TEXT NOT NULL,revision INTEGER NOT NULL,content_hash TEXT NOT NULL,dimensions INTEGER NOT NULL,data BLOB NOT NULL,PRIMARY KEY(id,kind));
            CREATE TABLE IF NOT EXISTS memory_jobs(id TEXT PRIMARY KEY,source_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,fingerprint TEXT NOT NULL,state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,error TEXT,project_id TEXT REFERENCES projects(id) ON DELETE CASCADE,UNIQUE(source_id,fingerprint));
            UPDATE memory_jobs SET state='queued' WHERE state='running';
            CREATE TRIGGER IF NOT EXISTS assistant_memory_insert AFTER INSERT ON memories BEGIN INSERT INTO assistant_fts VALUES(new.id,'memory',new.content); END;
            CREATE TRIGGER IF NOT EXISTS assistant_memory_update AFTER UPDATE ON memories BEGIN DELETE FROM assistant_fts WHERE id=old.id AND kind='memory'; INSERT INTO assistant_fts VALUES(new.id,'memory',new.content); DELETE FROM assistant_vectors WHERE id=old.id AND kind='memory'; END;
            CREATE TRIGGER IF NOT EXISTS assistant_memory_delete AFTER DELETE ON memories BEGIN DELETE FROM assistant_fts WHERE id=old.id AND kind='memory'; DELETE FROM assistant_vectors WHERE id=old.id AND kind='memory'; END;
            CREATE TRIGGER IF NOT EXISTS assistant_chunk_insert AFTER INSERT ON document_chunks BEGIN INSERT INTO assistant_fts VALUES(new.id,'document',new.content); END;
            CREATE TRIGGER IF NOT EXISTS assistant_chunk_delete AFTER DELETE ON document_chunks BEGIN DELETE FROM assistant_fts WHERE id=old.id AND kind='document'; DELETE FROM assistant_vectors WHERE id=old.id AND kind='document'; END;
            CREATE TRIGGER IF NOT EXISTS assistant_project_delete BEFORE DELETE ON projects BEGIN DELETE FROM memories WHERE id IN (SELECT memory_id FROM memory_info WHERE project_id=old.id); END;
            INSERT INTO assistant_fts SELECT id,'memory',content FROM memories WHERE NOT EXISTS(SELECT 1 FROM assistant_fts WHERE assistant_fts.id=memories.id AND kind='memory');
            PRAGMA user_version=2;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using (var columns = connection.CreateCommand())
        {
            columns.Transaction = transaction; columns.CommandText = "PRAGMA table_info(memory_jobs)"; var hasProject = false;
            using (var reader = await columns.ExecuteReaderAsync(token).ConfigureAwait(false)) while (await reader.ReadAsync(token).ConfigureAwait(false)) hasProject |= reader.GetString(1) == "project_id";
            if (!hasProject) { columns.CommandText = "ALTER TABLE memory_jobs ADD COLUMN project_id TEXT REFERENCES projects(id) ON DELETE CASCADE; UPDATE memory_jobs SET state='canceled';"; await columns.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private static async Task SaveMemoryInfoAsync(SqliteConnection connection, SqliteTransaction transaction, AssistantMemory memory, CancellationToken token)
    {
        if (memory.Kind is not ("profile" or "preference" or "habit" or "episode" or "goal" or "project_fact") || memory.State is not ("active" or "superseded") || memory.CloudPolicy is not ("deny" or "ask" or "allow") ||
            memory.Key.Length > 200 || !double.IsFinite(memory.Confidence) || memory.Confidence is < 0 or > 1 || !double.IsFinite(memory.Importance) || memory.Importance is < 0 or > 1) throw new ArgumentException("记忆元数据无效。");
        if (AssistantPrivacy.SecretReason(memory.Content + "\n" + memory.Key) is not null) throw new InvalidOperationException("记忆可能包含凭据，不能保存。");
        if (memory.SourceMessageId is { } source)
        {
            using var scope = Command(connection, "SELECT COUNT(*) FROM messages m LEFT JOIN session_projects p ON p.session_id=m.session_id WHERE m.id=$source AND p.project_id IS $project", ("$source", source.ToString()), ("$project", memory.ProjectId?.ToString())); scope.Transaction = transaction;
            if (Convert.ToInt32(await scope.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("记忆来源与项目作用域不一致。");
        }
        if (memory.Enabled && memory.State == "active" && memory.Key.Length > 0)
        {
            using var conflict = Command(connection, "SELECT COUNT(*) FROM memories m JOIN memory_info i ON i.memory_id=m.id WHERE m.id<>$id AND m.enabled=1 AND i.state='active' AND i.project_id IS $project AND i.memory_key=$key", ("$id", memory.Id.ToString()), ("$project", memory.ProjectId?.ToString()), ("$key", memory.Key)); conflict.Transaction = transaction;
            if (Convert.ToInt32(await conflict.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0) throw new InvalidOperationException("此分类已有有效记忆，请编辑原记忆或审核候选后替换，不能静默保留冲突事实。");
        }
        using var command = Command(connection, """
            INSERT INTO memory_info VALUES($id,$project,$kind,$key,$state,$confidence,$importance,$expires,$policy)
            ON CONFLICT(memory_id) DO UPDATE SET project_id=excluded.project_id,kind=excluded.kind,memory_key=excluded.memory_key,state=excluded.state,confidence=excluded.confidence,importance=excluded.importance,expires=excluded.expires,cloud_policy=excluded.cloud_policy
            """, ("$id", memory.Id.ToString()), ("$project", memory.ProjectId?.ToString()), ("$kind", memory.Kind), ("$key", memory.Key), ("$state", memory.State), ("$confidence", memory.Confidence), ("$importance", memory.Importance), ("$expires", memory.ExpiresAt is { } expires ? Stamp(expires) : null), ("$policy", memory.CloudPolicy));
        command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<AssistantMemory> ReadMemoryInfoAsync(SqliteConnection connection, AssistantMemory memory, CancellationToken token)
    {
        using var command = Command(connection, "SELECT project_id,kind,memory_key,state,confidence,importance,expires,cloud_policy FROM memory_info WHERE memory_id=$id", ("$id", memory.Id.ToString()));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? memory with { ProjectId = reader.IsDBNull(0) ? null : Guid.Parse(reader.GetString(0)), Kind = reader.GetString(1), Key = reader.GetString(2), State = reader.GetString(3), Confidence = reader.GetDouble(4), Importance = reader.GetDouble(5), ExpiresAt = reader.IsDBNull(6) ? null : ReadStamp(reader.GetString(6)), CloudPolicy = reader.GetString(7) } : memory;
    }

    public async Task<AssistantProject> CreateProjectAsync(string name, CancellationToken token)
    {
        Content(name, 100); var project = new AssistantProject(Guid.NewGuid(), name.Trim(), DateTimeOffset.UtcNow);
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "INSERT INTO projects VALUES($id,$name,$created)", ("$id", project.Id.ToString()), ("$name", project.Name), ("$created", Stamp(project.CreatedAt)));
        using (var count = Command(connection, "SELECT COUNT(*) FROM projects")) if (Convert.ToInt32(await count.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) >= 200) throw new InvalidOperationException("项目已达到200个，请先整理。");
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); return project;
    }
    public async Task<IReadOnlyList<AssistantProject>> ListProjectsAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,name,created FROM projects ORDER BY name LIMIT 200"); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantProject>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), ReadStamp(reader.GetString(2)))); return result;
    }
    public Task DeleteProjectAsync(Guid id, CancellationToken token) => ExecuteKnowledgeAsync("DELETE FROM projects WHERE id=$id", id, token);
    public async Task SetSessionProjectAsync(Guid session, Guid? project, CancellationToken token)
    {
        var old = await GetSessionProjectAsync(session, token).ConfigureAwait(false);
        if (old == project) return;
        if ((await ReadMessagesAsync(session, token).ConfigureAwait(false)).Count > 0) throw new InvalidOperationException("已有消息的会话不能切换项目，请新建会话后选择项目，避免资料跨项目。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "INSERT INTO session_projects VALUES($session,$project) ON CONFLICT(session_id) DO UPDATE SET project_id=excluded.project_id", ("$session", session.ToString()), ("$project", project?.ToString())); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    public async Task<Guid?> GetSessionProjectAsync(Guid session, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT project_id FROM session_projects WHERE session_id=$id", ("$id", session.ToString()));
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false); return value is string text ? Guid.Parse(text) : null;
    }
    private async Task ExecuteKnowledgeAsync(string sql, Guid id, CancellationToken token)
    { using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, sql, ("$id", id.ToString())); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke(); }

    public async Task<AssistantDocument> SaveDocumentAsync(AssistantDocument document, IReadOnlyList<AssistantPassage> passages, CancellationToken token)
        => await Task.Run(() => SaveDocumentCoreAsync(document, passages, token), token).ConfigureAwait(false);
    private async Task<AssistantDocument> SaveDocumentCoreAsync(AssistantDocument document, IReadOnlyList<AssistantPassage> passages, CancellationToken token)
    {
        Content(document.Name, 200);
        // The importer allows 2M source characters plus 120-character overlap per chunk.
        if (document.Id == Guid.Empty || document.SourcePath.Length > 4096 || document.Sha256.Length != 64 || document.CloudPolicy is not ("deny" or "ask" or "allow") || passages.Count is < 1 or > 2000 || passages.Any(chunk => chunk.DocumentId != document.Id || chunk.Id == Guid.Empty || string.IsNullOrWhiteSpace(chunk.Content) || chunk.Content.Length > 4000 || chunk.Locator.Length > 200) || passages.Sum(chunk => (long)chunk.Content.Length) > 2_240_000) throw new ArgumentException("知识文档或分块无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction();
        using (var capacity = Command(connection, "SELECT (SELECT COUNT(*) FROM documents WHERE id<>$id),(SELECT COUNT(*) FROM document_chunks WHERE document_id<>$id)", ("$id", document.Id.ToString())))
        { capacity.Transaction = transaction; using var reader = await capacity.ExecuteReaderAsync(token).ConfigureAwait(false); await reader.ReadAsync(token).ConfigureAwait(false); if (reader.GetInt32(0) >= 500 || reader.GetInt32(1) + passages.Count > 20000) throw new InvalidOperationException("知识库上限为500份文档、20000段，请先整理后导入。"); }
        using (var duplicate = Command(connection, "SELECT id FROM documents WHERE project_id IS $project AND sha=$sha AND id<>$id", ("$project", document.ProjectId?.ToString()), ("$sha", document.Sha256), ("$id", document.Id.ToString())))
        { duplicate.Transaction = transaction; if (await duplicate.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) throw new InvalidOperationException("此项目已导入相同内容，请使用已有文档。"); }
        using (var revision = Command(connection, "SELECT revision FROM documents WHERE id=$id", ("$id", document.Id.ToString())))
        { revision.Transaction = transaction; var current = await revision.ExecuteScalarAsync(token).ConfigureAwait(false); if (current is not null && Convert.ToInt32(current, CultureInfo.InvariantCulture) != document.Revision) throw new InvalidOperationException("文档已更新，请重新读取。"); document = document with { Revision = current is null ? 1 : checked(document.Revision + 1) }; }
        using (var command = Command(connection, """
            INSERT INTO documents VALUES($id,$project,$name,$source,$sha,$enabled,$policy,$revision,$created)
            ON CONFLICT(id) DO UPDATE SET project_id=excluded.project_id,name=excluded.name,source=excluded.source,sha=excluded.sha,enabled=excluded.enabled,cloud_policy=excluded.cloud_policy,revision=excluded.revision;
            DELETE FROM document_chunks WHERE document_id=$id;
            """, ("$id", document.Id.ToString()), ("$project", document.ProjectId?.ToString()), ("$name", document.Name), ("$source", document.SourcePath), ("$sha", document.Sha256), ("$enabled", document.Enabled ? 1 : 0), ("$policy", document.CloudPolicy), ("$revision", document.Revision), ("$created", Stamp(document.CreatedAt))))
        { command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        foreach (var chunk in passages)
        { using var command = Command(connection, "INSERT INTO document_chunks VALUES($id,$doc,$ordinal,$locator,$content)", ("$id", chunk.Id.ToString()), ("$doc", chunk.DocumentId.ToString()), ("$ordinal", chunk.Ordinal), ("$locator", chunk.Locator), ("$content", chunk.Content)); command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await transaction.CommitAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke(); return document;
    }
    public async Task<IReadOnlyList<AssistantDocument>> ListDocumentsAsync(Guid? project, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,project_id,name,source,sha,enabled,cloud_policy,revision,created FROM documents WHERE project_id IS $project ORDER BY name LIMIT 500", ("$project", project?.ToString())); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantDocument>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0, reader.GetString(6), ReadStamp(reader.GetString(8))) { Revision = reader.GetInt32(7) }); return result;
    }
    public async Task SetDocumentEnabledAsync(Guid id, bool enabled, CancellationToken token)
    { using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "UPDATE documents SET enabled=$enabled WHERE id=$id", ("$enabled", enabled ? 1 : 0), ("$id", id.ToString())); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke(); }
    public Task DeleteDocumentAsync(Guid id, CancellationToken token) => ExecuteKnowledgeAsync("DELETE FROM documents WHERE id=$id", id, token);
    public async Task<IReadOnlyList<AssistantPassage>> ReadDocumentPassagesAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,ordinal,locator,content FROM document_chunks WHERE document_id=$id ORDER BY ordinal LIMIT 2000", ("$id", id.ToString())); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantPassage>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), id, reader.GetInt32(1), reader.GetString(2), reader.GetString(3))); return result;
    }
    public async Task<AssistantMessage?> ReadSourceMessageAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT session_id,role,content,created FROM messages WHERE id=$id", ("$id", id.ToString())); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? new(id, Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), ReadStamp(reader.GetString(3))) : null;
    }

    public async Task<IReadOnlyList<AssistantRecall>> ListRecallItemsAsync(Guid? project, bool memories, bool documents, CancellationToken token)
        => await Task.Run(() => ListRecallItemsCoreAsync(project, memories, documents, token), token).ConfigureAwait(false);
    private async Task<IReadOnlyList<AssistantRecall>> ListRecallItemsCoreAsync(Guid? project, bool memories, bool documents, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, """
            SELECT m.id,'memory',m.content,COALESCE(i.kind,'preference'),COALESCE(m.source_id,'手动记忆'),i.project_id,COALESCE(i.cloud_policy,'ask'),m.revision,COALESCE(i.importance,0.5)
            FROM memories m LEFT JOIN memory_info i ON i.memory_id=m.id WHERE $memory=1 AND m.enabled=1 AND COALESCE(i.state,'active')='active' AND (i.project_id IS NULL OR i.project_id=$project) AND (i.expires IS NULL OR i.expires>$now)
            UNION ALL
            SELECT c.id,'document',c.content,d.name,c.locator,d.project_id,d.cloud_policy,d.revision,0.5 FROM document_chunks c JOIN documents d ON d.id=c.document_id
            WHERE $documents=1 AND d.enabled=1 AND (d.project_id IS NULL OR d.project_id=$project) LIMIT 20000
            """, ("$project", project?.ToString()), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$memory", memories ? 1 : 0), ("$documents", documents ? 1 : 0));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantRecall>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), reader.GetString(6), reader.GetInt32(7), reader.GetDouble(8))); return result;
    }

    public async Task<IReadOnlyList<AssistantRecall>> RecallAsync(string query, Guid? project, bool memories, bool documents, int budget, CancellationToken token)
    {
        if (query.Length > 32000 || budget is < 1 or > 200000) throw new ArgumentException("检索查询或预算无效。");
        var terms = RecallTerms(query); if (terms.Count == 0) return [];
        var ranks = new Dictionary<Guid, int>(); using var connection = await OpenAsync(token).ConfigureAwait(false);
        if (terms.Any(term => term.Length >= 3))
        {
            var match = string.Join(" OR ", terms.Where(term => term.Length >= 3).Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
            using var command = Command(connection, "SELECT id FROM assistant_fts WHERE assistant_fts MATCH $query ORDER BY bm25(assistant_fts) LIMIT 200", ("$query", match)); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) ranks.TryAdd(Guid.Parse(reader.GetString(0)), ranks.Count + 1);
        }
        var candidates = await ListRecallItemsAsync(project, memories, documents, token).ConfigureAwait(false);
        var selected = new List<AssistantRecall>();
        foreach (var hit in candidates.Select(item => item with { Score = (ranks.TryGetValue(item.Id, out var rank) ? 1d / (60 + rank) : 0) + terms.Count(term => item.Content.Contains(term, StringComparison.OrdinalIgnoreCase)) * .02 + item.Score * .001 })
            .Where(item => item.Score > .001).OrderByDescending(item => item.Score).ThenBy(item => item.Id).Take(20))
        { if (hit.Content.Length + 150 > budget) continue; selected.Add(hit); budget -= hit.Content.Length + 150; }
        return selected;
    }
    public static IReadOnlyList<string> RecallTerms(string query)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in QueryWords().Matches(query))
        { var value = match.Value; if (value.Length <= 40) result.Add(value); if (value.Any(character => character is >= '\u3400' and <= '\u9fff')) for (var index = 0; index + 3 <= value.Length && result.Count < 32; index++) result.Add(value.Substring(index, 3)); if (result.Count >= 32) break; }
        return result.Take(32).ToArray();
    }
    [GeneratedRegex("[\\p{L}\\p{N}]{1,100}", RegexOptions.CultureInvariant)] private static partial Regex QueryWords();

    private static string CandidateHash(Guid? project, string key, string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project + "\n" + key.Trim().Normalize().ToUpperInvariant() + "\n" + content.Trim().Normalize().ToUpperInvariant())));
    public async Task SaveCandidateAsync(AssistantMemoryCandidate candidate, CancellationToken token)
    {
        Content(candidate.Content, 4000); Content(candidate.Key, 200); if (candidate.Id == Guid.Empty || candidate.Confidence is < 0 or > 1 || !double.IsFinite(candidate.Confidence) || AssistantPrivacy.SecretReason(candidate.Content) is not null) throw new ArgumentException("候选记忆无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction(); var hash = CandidateHash(candidate.ProjectId, candidate.Key, candidate.Content);
        using (var check = Command(connection, "SELECT (SELECT COUNT(*) FROM candidate_suppressions WHERE hash=$hash)+(SELECT COUNT(*) FROM memory_suppressions WHERE source_id=$source)", ("$hash", hash), ("$source", candidate.SourceMessageId.ToString())))
        { check.Transaction = transaction; if (Convert.ToInt32(await check.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0) return; }
        using (var source = Command(connection, "SELECT COUNT(*) FROM messages m LEFT JOIN session_projects p ON p.session_id=m.session_id WHERE m.id=$id AND m.role='user' AND p.project_id IS $project", ("$id", candidate.SourceMessageId.ToString()), ("$project", candidate.ProjectId?.ToString())))
        { source.Transaction = transaction; if (Convert.ToInt32(await source.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("候选记忆缺少真实用户消息来源。"); }
        using (var command = Command(connection, "INSERT OR IGNORE INTO memory_candidates VALUES($id,$source,$project,$payload,$hash,NULL); INSERT OR IGNORE INTO candidate_sources SELECT id,$source FROM memory_candidates WHERE hash=$hash;", ("$id", candidate.Id.ToString()), ("$source", candidate.SourceMessageId.ToString()), ("$project", candidate.ProjectId?.ToString()), ("$payload", JsonSerializer.Serialize(candidate)), ("$hash", hash)))
        { command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<AssistantMemoryCandidate>> ListCandidatesAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT c.payload,(SELECT COUNT(DISTINCT m.session_id) FROM candidate_sources s JOIN messages m ON m.id=s.source_id WHERE s.candidate_id=c.id) FROM memory_candidates c WHERE c.approved_memory IS NULL LIMIT 500"); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantMemoryCandidate>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(JsonSerializer.Deserialize<AssistantMemoryCandidate>(reader.GetString(0))! with { EvidenceCount = reader.GetInt32(1) });
        reader.Close();
        for (var index = 0; index < result.Count; index++)
        {
            var candidate = result[index]; using var conflict = Command(connection, "SELECT m.id,m.revision FROM memories m JOIN memory_info i ON i.memory_id=m.id WHERE i.project_id IS $project AND i.memory_key=$key AND m.enabled=1 AND i.state='active' LIMIT 1", ("$project", candidate.ProjectId?.ToString()), ("$key", candidate.Key)); using var conflictReader = await conflict.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await conflictReader.ReadAsync(token).ConfigureAwait(false)) result[index] = candidate with { ConflictId = Guid.Parse(conflictReader.GetString(0)), ConflictRevision = conflictReader.GetInt32(1) };
        }
        return result;
    }
    public async Task RejectCandidateAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "INSERT OR IGNORE INTO candidate_suppressions SELECT hash FROM memory_candidates WHERE id=$id; DELETE FROM memory_candidates WHERE id=$id AND approved_memory IS NULL", ("$id", id.ToString())); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    public async Task<AssistantMemory> ApproveCandidateAsync(Guid id, Guid? replaceId, int? expectedRevision, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction(); AssistantMemoryCandidate candidate;
        using (var command = Command(connection, "SELECT payload FROM memory_candidates WHERE id=$id AND approved_memory IS NULL", ("$id", id.ToString())))
        { command.Transaction = transaction; candidate = JsonSerializer.Deserialize<AssistantMemoryCandidate>(await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string ?? throw new InvalidOperationException("候选已处理或删除。"))!; }
        using (var conflict = Command(connection, "SELECT m.id,m.revision FROM memories m JOIN memory_info i ON i.memory_id=m.id WHERE i.project_id IS $project AND i.memory_key=$key AND m.enabled=1 AND i.state='active' LIMIT 1", ("$project", candidate.ProjectId?.ToString()), ("$key", candidate.Key)))
        {
            conflict.Transaction = transaction; using var reader = await conflict.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            { if (replaceId?.ToString() != reader.GetString(0) || expectedRevision != reader.GetInt32(1)) throw new InvalidOperationException("存在冲突记忆，需明确审核替换且版本未变化。"); }
            else if (replaceId is not null) throw new InvalidOperationException("冲突已变化，请刷新。");
        }
        if (replaceId is { } old)
        {
            using var retire = Command(connection, "UPDATE memories SET enabled=0,revision=revision+1 WHERE id=$old; UPDATE memory_info SET state='superseded' WHERE memory_id=$old; INSERT INTO memory_revisions SELECT id,revision,content,$updated FROM memories WHERE id=$old;", ("$old", old.ToString()), ("$updated", Stamp(DateTimeOffset.UtcNow))); retire.Transaction = transaction; await retire.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var memory = new AssistantMemory(Guid.NewGuid(), candidate.Content, candidate.SourceMessageId, 1, true, DateTimeOffset.UtcNow) { ProjectId = candidate.ProjectId, Kind = candidate.Kind, Key = candidate.Key, Confidence = candidate.Confidence };
        using (var command = Command(connection, "INSERT INTO memories VALUES($id,$content,$source,1,1,$updated); INSERT INTO memory_revisions VALUES($id,1,$content,$updated); UPDATE memory_candidates SET approved_memory=$id WHERE id=$candidate", ("$id", memory.Id.ToString()), ("$content", memory.Content), ("$source", memory.SourceMessageId?.ToString()), ("$updated", Stamp(memory.UpdatedAt)), ("$candidate", id.ToString())))
        { command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await SaveMemoryInfoAsync(connection, transaction, memory, token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false); LibraryInvalidated?.Invoke(); return memory;
    }
    public async Task<IReadOnlyList<AssistantMemoryRevision>> ReadMemoryRevisionsAsync(Guid id, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT revision,content,updated FROM memory_revisions WHERE memory_id=$id ORDER BY revision DESC", ("$id", id.ToString())); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantMemoryRevision>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(new(reader.GetInt32(0), reader.GetString(1), ReadStamp(reader.GetString(2)))); return result;
    }
}
