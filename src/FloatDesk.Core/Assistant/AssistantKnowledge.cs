namespace FloatDesk.Core.Assistant;

public sealed record AssistantProject(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record AssistantDocument(Guid Id, Guid? ProjectId, string Name, string SourcePath, string Sha256, bool Enabled, string CloudPolicy, DateTimeOffset CreatedAt)
{ public int Revision { get; init; } = 1; }
public sealed record AssistantPassage(Guid Id, Guid DocumentId, int Ordinal, string Locator, string Content);
public sealed record AssistantRecall(Guid Id, string Kind, string Content, string Title, string Locator, Guid? ProjectId, string CloudPolicy, int Revision, double Score);
public sealed record AssistantMemoryCandidate(Guid Id, Guid SourceMessageId, Guid? ProjectId, string Kind, string Key, string Content, double Confidence, DateTimeOffset CreatedAt)
{ public int EvidenceCount { get; init; } = 1; public Guid? ConflictId { get; init; } public int? ConflictRevision { get; init; } }
public sealed record AssistantMemoryRevision(int Revision, string Content, DateTimeOffset UpdatedAt);
public sealed record AssistantCitation(Guid Id, string Kind, string Title, string Locator, int Revision);
public sealed record AssistantVector(Guid Id, string Kind, string Fingerprint, int Revision, string ContentHash, float[] Values);
public sealed record AssistantLearningJob(Guid Id, Guid SourceId, Guid SessionId, Guid? ProjectId, string Fingerprint, int Attempts);
public sealed record AssistantLearningSummary(int Queued, int Running, int Done, int Failed, string? LastFailure);
public sealed record AssistantLibrarySettings
{
    public bool UseKnowledge { get; init; }
    public bool SearchHistory { get; init; }
    public bool EmbeddingEnabled { get; init; }
    public Guid? EmbeddingProfileId { get; init; }
    public bool ExtractCandidates { get; init; }
    public Guid? ExtractionProfileId { get; init; }
    public int RecallLimit { get; init; } = 8;
}
public static class AssistantLibraryPolicy
{
    public static AssistantApiProfile LocalProfile(AssistantSettings settings, Guid? id)
    {
        var profile = AssistantConnections.WithLegacyProfile(settings).Profiles.FirstOrDefault(profile => profile.Id == id) ?? throw new InvalidOperationException("请先配置并选择本地模型连接。");
        if (!profile.Enabled || !profile.ModelEnabled || !AssistantEndpointPolicy.IsValid(profile.Endpoint) || !AssistantEndpointPolicy.IsLocal(new Uri(profile.Endpoint))) throw new InvalidOperationException("记忆提取和向量索引只使用已启用的回环本地模型，不会自动改用云端。");
        return profile;
    }
}
public interface IAssistantKnowledgeStore
{
    event Action? LibraryInvalidated;
    Task<AssistantProject> CreateProjectAsync(string name, CancellationToken token);
    Task<IReadOnlyList<AssistantProject>> ListProjectsAsync(CancellationToken token);
    Task DeleteProjectAsync(Guid id, CancellationToken token);
    Task SetSessionProjectAsync(Guid session, Guid? project, CancellationToken token);
    Task<Guid?> GetSessionProjectAsync(Guid session, CancellationToken token);
    Task<AssistantDocument> SaveDocumentAsync(AssistantDocument document, IReadOnlyList<AssistantPassage> passages, CancellationToken token);
    Task<IReadOnlyList<AssistantDocument>> ListDocumentsAsync(Guid? project, CancellationToken token);
    Task<IReadOnlyList<AssistantPassage>> ReadDocumentPassagesAsync(Guid id, CancellationToken token);
    Task<AssistantMessage?> ReadSourceMessageAsync(Guid id, CancellationToken token);
    Task SetDocumentEnabledAsync(Guid id, bool enabled, CancellationToken token);
    Task DeleteDocumentAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<AssistantRecall>> RecallAsync(string query, Guid? project, bool memories, bool documents, int budget, CancellationToken token);
    Task<IReadOnlyList<AssistantRecall>> RecallHistoryAsync(string query, Guid? project, int budget, Guid? excludeMessage, CancellationToken token);
    Task<IReadOnlyList<AssistantRecall>> ListRecallItemsAsync(Guid? project, bool memories, bool documents, CancellationToken token);
    Task SaveCandidateAsync(AssistantMemoryCandidate candidate, CancellationToken token);
    Task<IReadOnlyList<AssistantMemoryCandidate>> ListCandidatesAsync(CancellationToken token);
    Task<AssistantMemory> ApproveCandidateAsync(Guid id, Guid? replaceId, int? expectedRevision, CancellationToken token);
    Task RejectCandidateAsync(Guid id, CancellationToken token);
    Task<IReadOnlyList<AssistantMemoryRevision>> ReadMemoryRevisionsAsync(Guid id, CancellationToken token);
    Task<bool> SaveVectorAsync(AssistantVector vector, CancellationToken token);
    Task<IReadOnlyList<AssistantVector>> ReadVectorsAsync(string fingerprint, CancellationToken token);
    Task ClearVectorsAsync(CancellationToken token);
    Task QueueLearningAsync(Guid source, string fingerprint, CancellationToken token);
    Task<AssistantLearningJob?> TakeLearningJobAsync(string fingerprint, CancellationToken token);
    Task CompleteLearningJobAsync(Guid id, string state, string? failureCode, CancellationToken token);
}
