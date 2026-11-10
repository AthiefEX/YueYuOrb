using FloatDesk.Core.Assistant;
using System.Globalization;

namespace FloatDesk.Infrastructure.Assistant;

public sealed partial class AssistantStore
{
    public async Task<bool> SaveVectorAsync(AssistantVector vector, CancellationToken token)
    {
        if (vector.Kind is not ("memory" or "document") || vector.Fingerprint.Length != 64 || vector.Values.Length is < 1 or > 8192 || vector.Values.Any(value => !float.IsFinite(value)) || vector.Values.All(value => value == 0)) throw new ArgumentException("向量格式无效。");
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var transaction = connection.BeginTransaction();
        using (var current = Command(connection, vector.Kind == "memory" ? "SELECT content,revision FROM memories WHERE id=$id" : "SELECT c.content,d.revision FROM document_chunks c JOIN documents d ON d.id=c.document_id WHERE c.id=$id", ("$id", vector.Id.ToString())))
        {
            current.Transaction = transaction; using var reader = await current.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false) || reader.GetInt32(1) != vector.Revision || AssistantEmbeddingClient.ContentHash(reader.GetString(0)) != vector.ContentHash) return false;
        }
        var bytes = new byte[vector.Values.Length * sizeof(float)]; Buffer.BlockCopy(vector.Values, 0, bytes, 0, bytes.Length);
        using var command = Command(connection, "INSERT INTO assistant_vectors VALUES($id,$kind,$fingerprint,$revision,$hash,$dimensions,$data) ON CONFLICT(id,kind) DO UPDATE SET fingerprint=excluded.fingerprint,revision=excluded.revision,content_hash=excluded.content_hash,dimensions=excluded.dimensions,data=excluded.data",
            ("$id", vector.Id.ToString()), ("$kind", vector.Kind), ("$fingerprint", vector.Fingerprint), ("$revision", vector.Revision), ("$hash", vector.ContentHash), ("$dimensions", vector.Values.Length), ("$data", bytes));
        command.Transaction = transaction; await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); await transaction.CommitAsync(token).ConfigureAwait(false); return true;
    }
    public async Task<IReadOnlyList<AssistantVector>> ReadVectorsAsync(string fingerprint, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "SELECT id,kind,revision,content_hash,dimensions,data FROM assistant_vectors WHERE fingerprint=$fingerprint LIMIT 20000", ("$fingerprint", fingerprint)); using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var result = new List<AssistantVector>();
        long totalBytes = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var dimensions = reader.GetInt32(4); var bytes = (byte[])reader[5]; if (dimensions is < 1 or > 8192 || bytes.Length != dimensions * sizeof(float)) throw new InvalidDataException("本地向量索引损坏，请重建。");
            totalBytes += bytes.Length; if (totalBytes > 64L * 1024 * 1024) throw new InvalidDataException("本地向量读取超过64MiB预算，请减少文档或使用较低维度模型；全文检索仍可用。");
            var values = new float[dimensions]; Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length); if (values.Any(value => !float.IsFinite(value))) throw new InvalidDataException("本地向量数值无效，请重建。");
            result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), fingerprint, reader.GetInt32(2), reader.GetString(3), values));
        }
        return result;
    }
    public async Task ClearVectorsAsync(CancellationToken token)
    { using var connection = await OpenAsync(token).ConfigureAwait(false); using var command = Command(connection, "DELETE FROM assistant_vectors"); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
}
