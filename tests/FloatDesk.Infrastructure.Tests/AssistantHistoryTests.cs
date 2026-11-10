using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantHistoryTests
{
    [Fact]
    public async Task ExistingMessagesAreBackfilledOnceAndDeletionClearsIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-History-Migrate-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(root, "assistant.db");
        try
        {
            Guid sessionId;
            using (var old = new AssistantStore(path))
            {
                var session = await old.CreateSessionAsync("旧会话", CancellationToken.None); sessionId = session.Id;
                await old.AppendMessageAsync(new(Guid.NewGuid(), session.Id, "assistant", "LegacyQuartz历史配置", DateTimeOffset.UtcNow.AddMonths(-3)), CancellationToken.None);
            }
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await db.OpenAsync(); using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TRIGGER assistant_chat_insert; DROP TRIGGER assistant_chat_delete; DROP TRIGGER assistant_chat_update; DROP TABLE assistant_chat_fts; DROP TABLE assistant_chat_index_version;"; await cmd.ExecuteNonQueryAsync();
            }
            using var current = new AssistantStore(path);
            Assert.Single(await current.RecallHistoryAsync("LegacyQuartz", null, 3000, null, CancellationToken.None));
            Assert.Single(await current.RecallHistoryAsync("LegacyQuartz", null, 3000, null, CancellationToken.None));
            await current.DeleteSessionAsync(sessionId, CancellationToken.None);
            Assert.Empty(await current.RecallHistoryAsync("LegacyQuartz", null, 3000, null, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OldChatsAreBoundedScopedAndRemovedWithSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-History-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            var a = await store.CreateProjectAsync("A", CancellationToken.None); var b = await store.CreateProjectAsync("B", CancellationToken.None);
            var session = await store.CreateSessionAsync("三个月前的讨论", CancellationToken.None); await store.SetSessionProjectAsync(session.Id, a.Id, CancellationToken.None);
            var message = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "ProjectQuartz配置使用蓝色主题。" + new string('字', 10000), DateTimeOffset.UtcNow.AddMonths(-3));
            await store.AppendMessageAsync(message, CancellationToken.None);
            await store.AppendMessageAsync(new(Guid.NewGuid(), session.Id, "tool", "ProjectQuartz工具私密输出", DateTimeOffset.UtcNow), CancellationToken.None);
            var hits = await store.RecallHistoryAsync("ProjectQuartz", a.Id, 3000, null, CancellationToken.None);
            Assert.Single(hits); Assert.Equal(message.Id, hits[0].Id); Assert.InRange(hits[0].Content.Length, 1, 1600); Assert.Equal("ask", hits[0].CloudPolicy);
            Assert.Empty(await store.RecallHistoryAsync("ProjectQuartz", b.Id, 3000, null, CancellationToken.None));
            Assert.Empty(await store.RecallHistoryAsync("ProjectQuartz", null, 3000, null, CancellationToken.None));
            Assert.Empty(await store.RecallHistoryAsync("ProjectQuartz", a.Id, 3000, message.Id, CancellationToken.None));
            await store.DeleteProjectAsync(a.Id, CancellationToken.None);
            Assert.Empty(await store.RecallHistoryAsync("ProjectQuartz", null, 3000, null, CancellationToken.None));
            await store.DeleteSessionAsync(session.Id, CancellationToken.None);
            Assert.Null(await store.ReadSourceMessageAsync(message.Id, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
