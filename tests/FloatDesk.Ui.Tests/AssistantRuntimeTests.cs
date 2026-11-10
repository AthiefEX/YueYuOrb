using System.Runtime.CompilerServices;
using FloatDesk.App.Assistant;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantRuntimeTests
{
    [Fact]
    public async Task DisablingActiveKeyCancelsToolAndLeavesAnUnknownOutcomeReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Key-Cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profile = new AssistantApiProfile(Guid.NewGuid(), "key", "https://example.test/v1", "test", true);
            var settings = new AssistantSettings { Enabled = true, Endpoint = profile.Endpoint, Model = profile.Model, Profiles = [profile], ActiveProfileId = profile.Id, SupportsTools = true, AllowComputerControl = true };
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var tools = new BlockingTools();
            using var runtime = new AssistantRuntime(() => settings, store, new ToolModel(), new Credentials(), tools);
            runtime.ConfirmTool = (_, _) => Task.FromResult(true);
            var session = await runtime.CreateSessionAsync("test", CancellationToken.None);
            var sending = runtime.SendAsync(session.Id, "act", CancellationToken.None); await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            settings = settings with { Profiles = [profile with { Enabled = false }] }; runtime.ApplySettings(settings);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            var messages = await store.ReadMessagesAsync(session.Id, CancellationToken.None);
            Assert.Contains(messages, message => message.Role == "tool" && message.ToolCallId == "call" && message.Content.Contains("回执", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class BlockingTools : IAssistantToolExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<AssistantToolSchema> GetSchemas() => [];
        public AssistantToolPreview Preview(AssistantToolCall call, Guid target) => new(call, target, "fixed", "test", "test", "control", false);
        public async Task<AssistantToolResult> ExecuteAsync(AssistantToolPreview preview, CancellationToken token) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(true, "unreachable"); }
    }

    [Fact]
    public async Task SearchToolUsesIndependentModelAndDoesNotSendChatHistoryToIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Search-Runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var search = new AssistantApiProfile(Guid.NewGuid(), "search", "https://search.test/v1", "search-model", true);
            var settings = new AssistantSettings { Enabled = true, Endpoint = "https://chat.test/v1", Model = "chat-model", SupportsTools = true, SearchEnabled = true, SearchProfileId = search.Id, Profiles = [search] };
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var model = new SearchModel();
            using var runtime = new AssistantRuntime(() => settings, store, model, new Credentials());
            runtime.ConfirmOutbound = (_, _) => Task.FromResult(true);
            var session = await runtime.CreateSessionAsync("search", CancellationToken.None);
            await runtime.SendAsync(session.Id, "PRIVATE-CONTEXT plus search request", CancellationToken.None);
            var request = Assert.Single(model.Requests, item => item.Settings.Model == "search-model");
            Assert.Equal(2, request.Messages.Count); Assert.DoesNotContain(request.Messages, item => item.Content.Contains("PRIVATE-CONTEXT", StringComparison.Ordinal));
            Assert.Null(request.Tools);
            var messages = await runtime.LoadMessagesAsync(session.Id, CancellationToken.None);
            Assert.Contains(messages, item => item.Role == "tool" && item.Content.Contains("https://source.test/page", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SearchModel : IAssistantModelClient
    {
        public List<AssistantChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken token)
        {
            Requests.Add(request); await Task.Yield(); token.ThrowIfCancellationRequested();
            if (request.Settings.Model == "search-model") yield return new("search answer") { Sources = [new("source", "https://source.test/page")] };
            else if (request.Messages[^1].Role == "tool") yield return new("answer with https://source.test/page");
            else { Assert.Contains(request.Tools!, schema => schema.Name == "web_search"); yield return new(ToolCall: new(0, "search-call", "web_search", "{\"query\":\"public query\"}")); }
            yield return new(IsComplete: true);
        }
    }

    [Fact]
    public async Task OperationGrantCannotCarryIntoAnotherConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Grant-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AssistantSettings { Enabled = true, Endpoint = "https://example.test/v1", Model = "test", SupportsTools = true, AllowComputerControl = true };
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var approvals = 0; var tools = new Tools();
            using var runtime = new AssistantRuntime(() => settings, store, new ToolModel(), new Credentials(), tools);
            runtime.ConfirmTool = (_, _) => { approvals++; return Task.FromResult(true); };
            var first = await runtime.CreateSessionAsync("first", CancellationToken.None);
            await runtime.SendAsync(first.Id, "act", CancellationToken.None);
            var second = await runtime.CreateSessionAsync("second", CancellationToken.None);
            await runtime.SendAsync(second.Id, "act", CancellationToken.None);
            Assert.Equal(2, approvals); Assert.Equal(2, tools.Executed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Tools : IAssistantToolExecutor
    {
        public int Executed { get; private set; }
        public IReadOnlyList<AssistantToolSchema> GetSchemas() => [];
        public AssistantToolPreview Preview(AssistantToolCall call, Guid target) => new(call, target, "fixed-target", "test", "test", "control", false);
        public Task<AssistantToolResult> ExecuteAsync(AssistantToolPreview preview, CancellationToken token) { Executed++; return Task.FromResult(new AssistantToolResult(true, "ok")); }
    }
    private sealed class ToolModel : IAssistantModelClient
    {
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            yield return request.Messages[^1].Role == "tool" ? new("done") : new(ToolCall: new(0, "call", "test_control", "{}"));
            yield return new(IsComplete: true);
        }
    }

    [Fact]
    public async Task DisablingAssistantCancelsInFlightGenerationAndRejectsLateOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Assistant-Runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AssistantSettings { Enabled = true, Endpoint = "https://example.test/v1", Model = "test" };
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var model = new BlockingModel();
            using var runtime = new AssistantRuntime(() => settings, store, model, new Credentials());
            var session = await runtime.CreateSessionAsync("test", CancellationToken.None);
            var received = new List<string>(); runtime.TextChanged += (_, text) => received.Add(text);
            var sending = runtime.SendAsync(session.Id, "hello", CancellationToken.None);
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            settings = settings with { Enabled = false }; runtime.ApplySettings(settings);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.False(runtime.IsBusy); Assert.DoesNotContain(received, text => text.Contains("late", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BlockingModel : IAssistantModelClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); yield return new("late"); }
    }
    private sealed class Credentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult<string?>(null);
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) => Task.CompletedTask;
    }
}
