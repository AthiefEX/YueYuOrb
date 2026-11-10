using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantStoreTests
{
    [Fact]
    public async Task MessagesSurviveReopenAndSessionDeletionClearsDerivedMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Assistant-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            await store.InitializeAsync(CancellationToken.None);
            var session = await store.CreateSessionAsync("test", CancellationToken.None);
            var message = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "ordinary test message", DateTimeOffset.UtcNow);
            await store.AppendMessageAsync(message, CancellationToken.None);
            var again = new AssistantStore(Path.Combine(root, "assistant.db"));
            Assert.Equal(message.Content, Assert.Single(await again.ReadMessagesAsync(session.Id, CancellationToken.None)).Content);
            await store.SaveMemoryAsync(new(Guid.NewGuid(), "derived", message.Id, 1, true, DateTimeOffset.UtcNow), CancellationToken.None);
            await store.DeleteSessionAsync(session.Id, CancellationToken.None);
            Assert.Empty(await store.ReadMessagesAsync(session.Id, CancellationToken.None));
            Assert.Empty(await store.ListMemoriesAsync(CancellationToken.None));
            Assert.Empty(await store.ListSessionsAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MemoryRevisionConflictAndDeletionCannotSilentlyReintroduceSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Assistant-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AssistantStore(Path.Combine(root, "assistant.db")); await store.InitializeAsync(CancellationToken.None);
            var session = await store.CreateSessionAsync("test", CancellationToken.None);
            var message = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "source", DateTimeOffset.UtcNow);
            await store.AppendMessageAsync(message, CancellationToken.None);
            var initial = await store.SaveMemoryAsync(new(Guid.NewGuid(), "first", message.Id, 1, true, DateTimeOffset.UtcNow), CancellationToken.None);
            var updated = await store.SaveMemoryAsync(initial with { Content = "second" }, CancellationToken.None);
            Assert.Equal(2, updated.Revision);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveMemoryAsync(initial with { Content = "stale" }, CancellationToken.None));
            await store.DeleteMemoryAsync(initial.Id, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveMemoryAsync(initial with { Id = Guid.NewGuid() }, CancellationToken.None));
            Assert.Empty(await store.ListMemoriesAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
