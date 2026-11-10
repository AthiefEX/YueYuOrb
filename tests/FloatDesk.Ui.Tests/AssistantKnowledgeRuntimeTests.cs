using System.Runtime.CompilerServices;
using FloatDesk.App.Assistant;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantKnowledgeRuntimeTests
{
    [Fact]
    public async Task OldChatRecallNeedsOptInAndCloudApprovalAndKeepsPayloadBounded()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-History-Runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            var old = await store.CreateSessionAsync("三个月前", CancellationToken.None);
            await store.AppendMessageAsync(new(Guid.NewGuid(), old.Id, "user", "QuartzLegacy使用蓝色方案" + new string('字', 12000), DateTimeOffset.UtcNow.AddMonths(-3)), CancellationToken.None);
            var settings = new AssistantSettings { Enabled = true, Endpoint = "https://example.test/v1", Model = "test" };
            var model = new Model(); using var runtime = new AssistantRuntime(() => settings, store, model, new Credentials()); var approvals = 0;
            runtime.ConfirmOutbound = (_, _) => { approvals++; return Task.FromResult(true); };
            var first = await runtime.CreateSessionAsync("无检索", CancellationToken.None); await runtime.SendAsync(first.Id, "QuartzLegacy是什么", CancellationToken.None);
            Assert.DoesNotContain(model.Requests[0].Messages, message => message.Content.Contains("蓝色方案", StringComparison.Ordinal));
            settings = settings with { Library = new() { SearchHistory = true } }; runtime.ApplySettings(settings);
            var second = await runtime.CreateSessionAsync("跨会话查找", CancellationToken.None); await runtime.SendAsync(second.Id, "QuartzLegacy", CancellationToken.None);
            Assert.Contains(model.Requests[1].Messages, message => message.Content.Contains("蓝色方案", StringComparison.Ordinal));
            Assert.True(model.Requests[1].Messages.Sum(message => message.Content.Length) < 6000); Assert.Equal(1, approvals);
            Assert.Contains((await store.ReadMessagesAsync(second.Id, CancellationToken.None))[^1].References, reference => reference.Kind == "chat");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CrossSessionKnowledgeUsesOnlySelectedProjectAndPersistsCitations()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Recall-Runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var a = await store.CreateProjectAsync("A", CancellationToken.None); var b = await store.CreateProjectAsync("B", CancellationToken.None);
            var doc = new AssistantDocument(Guid.NewGuid(), a.Id, "palette.txt", "local", new string('a', 64), true, "ask", DateTimeOffset.UtcNow); await store.SaveDocumentAsync(doc, [new(Guid.NewGuid(), doc.Id, 0, "段1", "项目主题颜色使用蓝色")], CancellationToken.None);
            var settings = new AssistantSettings { Enabled = true, Endpoint = "https://example.test/v1", Model = "test", Library = new() { UseKnowledge = true } };
            var model = new Model(); using var runtime = new AssistantRuntime(() => settings, store, model, new Credentials()); var approvals = 0; runtime.ConfirmOutbound = (_, _) => { approvals++; return Task.FromResult(true); };
            var sessionA = await runtime.CreateSessionAsync("A chat", CancellationToken.None); await runtime.SetProjectAsync(sessionA.Id, a.Id, CancellationToken.None); await runtime.SendAsync(sessionA.Id, "主题颜色是什么", CancellationToken.None);
            Assert.Contains(model.Requests[0].Messages, item => item.Content.Contains("项目主题颜色使用蓝色", StringComparison.Ordinal)); Assert.Equal(1, approvals);
            var stored = await store.ReadMessagesAsync(sessionA.Id, CancellationToken.None); Assert.NotEmpty(stored[^1].References);
            var sessionB = await runtime.CreateSessionAsync("B chat", CancellationToken.None); await runtime.SetProjectAsync(sessionB.Id, b.Id, CancellationToken.None); await runtime.SendAsync(sessionB.Id, "主题颜色是什么", CancellationToken.None);
            Assert.DoesNotContain(model.Requests[1].Messages, item => item.Content.Contains("蓝色", StringComparison.Ordinal)); Assert.Equal(1, approvals);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.SetProjectAsync(sessionB.Id, a.Id, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Model : IAssistantModelClient
    {
        public List<AssistantChatRequest> Requests { get; } = [];
        public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken token)
        { Requests.Add(request); await Task.Yield(); token.ThrowIfCancellationRequested(); yield return new("依据引用资料回答。"); yield return new(IsComplete: true); }
    }
    private sealed class Credentials : IAssistantCredentialStore
    {
        public Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token) => Task.FromResult<string?>(null);
        public Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token) => Task.CompletedTask;
        public Task DeleteAsync(string purpose, string endpoint, CancellationToken token) => Task.CompletedTask;
    }
}
