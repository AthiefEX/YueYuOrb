using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed partial class AssistantStore
{
    public event Action? LibraryInvalidated;
    public async Task<AssistantLearningSummary> LearningSummaryAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT SUM(CASE WHEN state='queued' THEN 1 ELSE 0 END),SUM(CASE WHEN state='running' THEN 1 ELSE 0 END),SUM(CASE WHEN state='done' THEN 1 ELSE 0 END),SUM(CASE WHEN state='failed' THEN 1 ELSE 0 END),(SELECT error FROM memory_jobs WHERE error IS NOT NULL ORDER BY rowid DESC LIMIT 1) FROM memory_jobs");
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); await reader.ReadAsync(token).ConfigureAwait(false);
        int Count(int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
        return new(Count(0), Count(1), Count(2), Count(3), reader.IsDBNull(4) ? null : reader.GetString(4));
    }
    public async Task RetryFailedLearningAsync(string fingerprint, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "UPDATE memory_jobs SET state='queued',attempts=0,error=NULL WHERE state='failed' AND fingerprint=$fingerprint", ("$fingerprint", fingerprint)); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    public async Task QueueLearningAsync(Guid source, string fingerprint, CancellationToken token)
    {
        if (fingerprint.Length != 64) throw new ArgumentException("提取模型指纹无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, """
            INSERT OR IGNORE INTO memory_jobs(id,source_id,fingerprint,state,attempts,error,project_id)
            SELECT $id,m.id,$fingerprint,'queued',0,NULL,p.project_id FROM messages m LEFT JOIN session_projects p ON p.session_id=m.session_id
            WHERE m.id=$source AND m.role='user' AND NOT EXISTS(SELECT 1 FROM memory_suppressions WHERE source_id=m.id)
            """, ("$id", Guid.NewGuid().ToString()), ("$source", source.ToString()), ("$fingerprint", fingerprint)); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    public async Task<AssistantLearningJob?> TakeLearningJobAsync(string fingerprint, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction(); AssistantLearningJob? job = null;
        using (var command = Command(connection, "SELECT j.id,j.source_id,m.session_id,j.project_id,j.attempts FROM memory_jobs j JOIN messages m ON m.id=j.source_id WHERE j.fingerprint=$fingerprint AND (j.state='queued' OR j.state='failed') AND j.attempts<3 ORDER BY j.rowid LIMIT 1", ("$fingerprint", fingerprint)))
        {
            command.Transaction = transaction; using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) job = new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), fingerprint, reader.GetInt32(4) + 1);
        }
        if (job is not null)
        { using var command = Command(connection, "UPDATE memory_jobs SET state='running',attempts=attempts+1,error=NULL WHERE id=$id", ("$id", job.Id.ToString())); command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        await transaction.CommitAsync(token).ConfigureAwait(false); return job;
    }
    public async Task CompleteLearningJobAsync(Guid id, string state, string? failureCode, CancellationToken token)
    {
        if (state is not ("done" or "failed" or "queued" or "canceled") || failureCode?.Length > 80 || failureCode?.Any(character => !char.IsAsciiLetterOrDigit(character)) == true) throw new ArgumentException("提取状态无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "UPDATE memory_jobs SET state=$state,error=$error WHERE id=$id", ("$state", state), ("$error", failureCode), ("$id", id.ToString())); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SuppressMemoryHashAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        string? hash = null;
        using (var read = Command(connection, "SELECT m.content,i.project_id,COALESCE(i.memory_key,'') FROM memories m LEFT JOIN memory_info i ON i.memory_id=m.id WHERE m.id=$id", ("$id", id.ToString())))
        {
            read.Transaction = transaction; using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false)) hash = CandidateHash(reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(0));
        }
        if (hash is not null) { using var command = Command(connection, "INSERT OR IGNORE INTO candidate_suppressions VALUES($hash)", ("$hash", hash)); command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
    }
}
