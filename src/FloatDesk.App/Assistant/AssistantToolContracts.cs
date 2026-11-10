using FloatDesk.Core.Assistant;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Assistant;

public sealed record AssistantToolPreview(AssistantToolCall Call, Guid EnvironmentId, string EnvironmentBinding, string Title, string Description, string Category, bool AlwaysConfirm)
{
    public DesktopSessionTask? TaskDefinition { get; init; }
    public int? ExpectedRevision { get; init; }
}
public sealed record AssistantToolResult(bool Success, string Content, bool Sensitive = false);

public interface IAssistantToolExecutor
{
    IReadOnlyList<AssistantToolSchema> GetSchemas();
    AssistantToolPreview Preview(AssistantToolCall request, Guid environmentId);
    Task<AssistantToolResult> ExecuteAsync(AssistantToolPreview preview, CancellationToken token);
}
