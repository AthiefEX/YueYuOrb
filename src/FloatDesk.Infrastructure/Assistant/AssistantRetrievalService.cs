using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed class AssistantRetrievalService(IAssistantKnowledgeStore store, AssistantEmbeddingClient embedding, IAssistantCredentialStore credentials)
{
    public event Action<string>? StatusChanged;
    public async Task<int> RebuildAsync(AssistantSettings settings, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        void Invalidate() => active.Cancel();
        store.LibraryInvalidated += Invalidate;
        try { return await RebuildCoreAsync(settings, progress, active.Token).ConfigureAwait(false); }
        finally { store.LibraryInvalidated -= Invalidate; }
    }
    private async Task<int> RebuildCoreAsync(AssistantSettings settings, IProgress<(int Done, int Total)>? progress, CancellationToken token)
    {
        var profile = AssistantLibraryPolicy.LocalProfile(settings, settings.Library.EmbeddingProfileId);
        var fingerprint = AssistantEmbeddingClient.Fingerprint(profile); var items = new Dictionary<(Guid Id, string Kind), AssistantRecall>();
        foreach (var scope in (await store.ListProjectsAsync(token).ConfigureAwait(false)).Select(project => (Guid?)project.Id).Prepend(null))
            foreach (var item in await store.ListRecallItemsAsync(scope, true, true, token).ConfigureAwait(false)) items.TryAdd((item.Id, item.Kind), item);
        if (items.Count > 20000) throw new InvalidOperationException("索引超过20000段，请先整理文档。");
        var key = await credentials.GetAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, token).ConfigureAwait(false);
        await store.ClearVectorsAsync(token).ConfigureAwait(false); var count = 0; var done = 0;
        foreach (var batch in items.Values.Chunk(8))
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in batch) await AssistantPrivacy.CheckStoredSecretsAsync(item.Content, settings, credentials, token).ConfigureAwait(false);
            var vectors = await embedding.EmbedAsync(profile, batch.Select(item => item.Content).ToArray(), key, token).ConfigureAwait(false);
            for (var index = 0; index < batch.Length; index++)
            { var item = batch[index]; if (await store.SaveVectorAsync(new(item.Id, item.Kind, fingerprint, item.Revision, AssistantEmbeddingClient.ContentHash(item.Content), vectors[index]), token).ConfigureAwait(false)) count++; done++; }
            progress?.Report((done, items.Count));
        }
        return count;
    }

    public async Task<IReadOnlyList<AssistantRecall>> RecallAsync(AssistantSettings settings, string query, Guid? project, int budget, CancellationToken token)
    {
        var lexical = await store.RecallAsync(query, project, settings.UseMemory, settings.Library.UseKnowledge, budget, token).ConfigureAwait(false);
        if (!settings.Library.EmbeddingEnabled) return lexical.Take(settings.Library.RecallLimit).ToArray();
        try
        {
            var profile = AssistantLibraryPolicy.LocalProfile(settings, settings.Library.EmbeddingProfileId);
            var fingerprint = AssistantEmbeddingClient.Fingerprint(profile); var vectors = await store.ReadVectorsAsync(fingerprint, token).ConfigureAwait(false);
            if (vectors.Count == 0) { StatusChanged?.Invoke("未建立当前模型的向量索引，已使用本地全文检索。"); return lexical.Take(settings.Library.RecallLimit).ToArray(); }
            await AssistantPrivacy.CheckStoredSecretsAsync(query, settings, credentials, token).ConfigureAwait(false);
            var key = await credentials.GetAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, token).ConfigureAwait(false);
            var queryVector = (await embedding.EmbedAsync(profile, [query], key, token).ConfigureAwait(false))[0];
            var allowed = (await store.ListRecallItemsAsync(project, settings.UseMemory, settings.Library.UseKnowledge, token).ConfigureAwait(false)).ToDictionary(item => (item.Id, item.Kind));
            var ranked = vectors.Where(vector => allowed.TryGetValue((vector.Id, vector.Kind), out var item) && item.Revision == vector.Revision && AssistantEmbeddingClient.ContentHash(item.Content) == vector.ContentHash)
                .Select(vector => (Vector: vector, Similarity: Cosine(queryVector, vector.Values))).Where(item => item.Similarity > .05).OrderByDescending(item => item.Similarity).Take(40).ToArray();
            var scores = new Dictionary<(Guid Id, string Kind), double>();
            for (var index = 0; index < lexical.Count; index++) scores[(lexical[index].Id, lexical[index].Kind)] = 1d / (60 + index + 1);
            for (var index = 0; index < ranked.Length; index++) { var id = (ranked[index].Vector.Id, ranked[index].Vector.Kind); scores[id] = scores.GetValueOrDefault(id) + 1d / (60 + index + 1); }
            var result = new List<AssistantRecall>();
            foreach (var pair in scores.OrderByDescending(pair => pair.Value))
            {
                if (!allowed.TryGetValue(pair.Key, out var item) || item.Content.Length + 150 > budget) continue;
                result.Add(item with { Score = pair.Value }); budget -= item.Content.Length + 150; if (result.Count >= settings.Library.RecallLimit) break;
            }
            return result;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or IOException or System.Net.Http.HttpRequestException)
        { StatusChanged?.Invoke("本地语义检索未就绪，已使用全文检索；未切换云服务。"); return lexical.Take(settings.Library.RecallLimit).ToArray(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { StatusChanged?.Invoke("本地向量接口超时，已使用全文检索。"); return lexical.Take(settings.Library.RecallLimit).ToArray(); }
    }

    public static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length || left.Length == 0) return 0;
        double dot = 0, a = 0, b = 0;
        for (var index = 0; index < left.Length; index++) { if (!float.IsFinite(left[index]) || !float.IsFinite(right[index])) return 0; dot += (double)left[index] * right[index]; a += (double)left[index] * left[index]; b += (double)right[index] * right[index]; }
        return a > 0 && b > 0 ? dot / Math.Sqrt(a * b) : 0;
    }
}
