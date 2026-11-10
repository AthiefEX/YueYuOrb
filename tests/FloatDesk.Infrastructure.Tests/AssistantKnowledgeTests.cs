using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantKnowledgeTests
{
    [Fact]
    public async Task OneHundredSyntheticIdentifiersRetrieveTheCorrectSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Recall-Eval-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var expected = new List<Guid>();
            for (var index = 0; index < 100; index++)
            {
                var document = new AssistantDocument(Guid.NewGuid(), null, "source-" + index, "local", index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), true, "deny", DateTimeOffset.UtcNow);
                var passage = new AssistantPassage(Guid.NewGuid(), document.Id, 0, "段1", $"方案标识T{index:0000}的主题方案记录。"); expected.Add(passage.Id); await store.SaveDocumentAsync(document, [passage], CancellationToken.None);
            }
            for (var index = 0; index < 100; index++)
            { var hits = await store.RecallAsync($"T{index:0000}", null, false, true, 5000, CancellationToken.None); Assert.Equal(expected[index], hits[0].Id); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConflictingCandidateRequiresRevisionBoundApprovalAndDeletionClearsVectors()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Conflict-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            var old = await store.SaveMemoryAsync(new(Guid.NewGuid(), "主题使用蓝色", null, 1, true, DateTimeOffset.UtcNow) { Key = "theme" }, CancellationToken.None);
            var fingerprint = new string('f', 64); Assert.True(await store.SaveVectorAsync(new(old.Id, "memory", fingerprint, 1, AssistantEmbeddingClient.ContentHash(old.Content), [1f, 0f]), CancellationToken.None));
            var session = await store.CreateSessionAsync("new", CancellationToken.None); var source = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "主题改成绿色", DateTimeOffset.UtcNow); await store.AppendMessageAsync(source, CancellationToken.None);
            var candidate = new AssistantMemoryCandidate(Guid.NewGuid(), source.Id, null, "preference", "theme", "主题使用绿色", .9, DateTimeOffset.UtcNow); await store.SaveCandidateAsync(candidate, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveCandidateAsync(candidate.Id, null, null, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ApproveCandidateAsync(candidate.Id, old.Id, 999, CancellationToken.None));
            var accepted = await store.ApproveCandidateAsync(candidate.Id, old.Id, old.Revision, CancellationToken.None);
            Assert.Contains(await store.RecallAsync("主题", null, true, false, 5000, CancellationToken.None), item => item.Id == accepted.Id);
            Assert.DoesNotContain(await store.RecallAsync("主题", null, true, false, 5000, CancellationToken.None), item => item.Id == old.Id);
            Assert.Empty(await store.ReadVectorsAsync(fingerprint, CancellationToken.None));
            Assert.False(await store.SaveVectorAsync(new(old.Id, "memory", fingerprint, 1, AssistantEmbeddingClient.ContentHash(old.Content), [1f, 0f]), CancellationToken.None));
            await store.DeleteMemoryAsync(accepted.Id, CancellationToken.None); Assert.Empty(await store.ReadMemoryRevisionsAsync(accepted.Id, CancellationToken.None));
            await store.SaveCandidateAsync(candidate with { Id = Guid.NewGuid() }, CancellationToken.None); Assert.Empty(await store.ListCandidatesAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ProjectRecallShortChineseTermsAndDeletesStayIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Library-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db"));
            var a = await store.CreateProjectAsync("项目A", CancellationToken.None); var b = await store.CreateProjectAsync("项目B", CancellationToken.None);
            var document = new AssistantDocument(Guid.NewGuid(), a.Id, "主题.txt", "local.txt", new string('a', 64), true, "ask", DateTimeOffset.UtcNow);
            await store.SaveDocumentAsync(document, [new(Guid.NewGuid(), document.Id, 0, "段1", "项目主题颜色是蓝色。")], CancellationToken.None);
            var memory = await store.SaveMemoryAsync(new(Guid.NewGuid(), "主题配色使用蓝色", null, 1, true, DateTimeOffset.UtcNow) { ProjectId = a.Id }, CancellationToken.None);
            Assert.NotEmpty(await store.RecallAsync("主题", a.Id, true, true, 5000, CancellationToken.None));
            Assert.Empty(await store.RecallAsync("主题", b.Id, true, true, 5000, CancellationToken.None));
            await store.DeleteDocumentAsync(document.Id, CancellationToken.None); await store.DeleteMemoryAsync(memory.Id, CancellationToken.None);
            Assert.Empty(await store.RecallAsync("主题", a.Id, true, true, 5000, CancellationToken.None));
            using var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "assistant.db"), Pooling = false }.ToString()); await database.OpenAsync(); using var count = database.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM assistant_fts"; Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CandidateNeedsApprovalAndRejectedSourceCannotReappear()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Candidate-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var session = await store.CreateSessionAsync("s", CancellationToken.None);
            var source = new AssistantMessage(Guid.NewGuid(), session.Id, "user", "我喜欢深色界面", DateTimeOffset.UtcNow); await store.AppendMessageAsync(source, CancellationToken.None);
            var candidate = new AssistantMemoryCandidate(Guid.NewGuid(), source.Id, null, "preference", "theme", "我喜欢深色界面", .9, DateTimeOffset.UtcNow);
            await store.SaveCandidateAsync(candidate, CancellationToken.None);
            Assert.Empty(await store.RecallAsync("界面", null, true, false, 5000, CancellationToken.None));
            Assert.Single(await store.ListCandidatesAsync(CancellationToken.None));
            await store.RejectCandidateAsync(candidate.Id, CancellationToken.None);
            await store.SaveCandidateAsync(candidate with { Id = Guid.NewGuid() }, CancellationToken.None);
            Assert.Empty(await store.ListCandidatesAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
