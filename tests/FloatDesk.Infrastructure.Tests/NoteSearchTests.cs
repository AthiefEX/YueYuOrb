using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Notes;

namespace FloatDesk.Infrastructure.Tests;

public sealed class NoteSearchTests
{
    [Fact]
    public async Task SearchMatchesBodyAsWellAsTitle()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "FloatDeskTests",
            Guid.NewGuid().ToString("N"));
        var repository = new SqliteNoteRepository(Path.Combine(root, "notes.db"));
        await repository.InitializeAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await repository.SaveAsync(
            new NoteDocument(
                Guid.NewGuid(),
                "会议记录",
                "正文包含唯一关键词 星河计划",
                1,
                now,
                now,
                null),
            CancellationToken.None);
        try
        {
            var results = await repository.SearchAsync(
                "星河计划",
                recycleBin: false,
                CancellationToken.None);

            Assert.Equal("会议记录", Assert.Single(results).Title);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
