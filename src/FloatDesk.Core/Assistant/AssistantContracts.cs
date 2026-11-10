using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatDesk.Core.Assistant;

public sealed record AssistantSession(Guid Id, string Title, DateTimeOffset CreatedAt);
public sealed record AssistantMessage(Guid Id, Guid SessionId, string Role, string Content, DateTimeOffset CreatedAt)
{
    public IReadOnlyList<AssistantToolCall> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }
    public IReadOnlyList<AssistantCitation> References { get; init; } = [];
    [JsonIgnore] public string? ExtractionFingerprint { get; init; }
}
public sealed record AssistantMemory(Guid Id, string Content, Guid? SourceMessageId, int Revision, bool Enabled, DateTimeOffset UpdatedAt)
{
    public Guid? ProjectId { get; init; }
    public string Kind { get; init; } = "preference";
    public string Key { get; init; } = "";
    public string State { get; init; } = "active";
    public double Confidence { get; init; } = 1;
    public double Importance { get; init; } = .5;
    public DateTimeOffset? ExpiresAt { get; init; }
    public string CloudPolicy { get; init; } = "ask";
}
public sealed record AssistantAsset(Guid Id, string Name, string Kind, string Text, string SourcePath, string Sha256);
public sealed record AssistantToolCall(string Id, string Name, string Arguments);
public sealed record AssistantToolFragment(int Index, string? Id, string? Name, string? Arguments);
public sealed record AssistantSource(string Title, string Url);
public sealed record AssistantChatDelta(string Text = "", AssistantToolFragment? ToolCall = null, bool IsComplete = false)
{
    public IReadOnlyList<AssistantSource> Sources { get; init; } = [];
}
public sealed record AssistantToolSchema(string Name, string Description, JsonElement Parameters);
public sealed record AssistantChatRequest(AssistantSettings Settings, IReadOnlyList<AssistantMessage> Messages,
    [property: JsonIgnore] string? ApiKey, IReadOnlyList<AssistantToolSchema>? Tools = null)
{
    [JsonIgnore] public bool ContainsSensitiveContext { get; init; }
    [JsonIgnore] public AssistantRequestApproval? Approval { get; init; }
}
public sealed record AssistantRequestApproval(string Endpoint, string Model, string Fingerprint, DateTimeOffset ExpiresAt);

public interface IAssistantModelClient
{
    IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, CancellationToken cancellationToken);
}

public interface IAssistantCredentialStore
{
    Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token);
    Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token);
    Task DeleteAsync(string purpose, string endpoint, CancellationToken token);
}

public interface IAssistantStore
{
    Task InitializeAsync(CancellationToken token);
    Task<AssistantSession> CreateSessionAsync(string title, CancellationToken token);
    Task<IReadOnlyList<AssistantSession>> ListSessionsAsync(CancellationToken token);
    Task AppendMessageAsync(AssistantMessage message, CancellationToken token);
    Task<IReadOnlyList<AssistantMessage>> ReadMessagesAsync(Guid sessionId, CancellationToken token);
    Task DeleteSessionAsync(Guid sessionId, CancellationToken token);
    Task<AssistantMemory> SaveMemoryAsync(AssistantMemory memory, CancellationToken token);
    Task<IReadOnlyList<AssistantMemory>> ListMemoriesAsync(CancellationToken token);
    Task DeleteMemoryAsync(Guid id, CancellationToken token);
    Task SaveAssetAsync(AssistantAsset asset, CancellationToken token);
    Task<IReadOnlyList<AssistantAsset>> ListAssetsAsync(CancellationToken token);
    Task DeleteAssetAsync(Guid id, CancellationToken token);
}
