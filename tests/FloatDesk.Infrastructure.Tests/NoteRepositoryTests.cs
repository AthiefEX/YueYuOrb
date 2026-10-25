using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Notes;

namespace FloatDesk.Infrastructure.Tests;

public sealed class NoteRepositoryTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "FloatDeskTests",
        Guid.NewGuid().ToString("N"),
        "notes.db");
    private SqliteNoteRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteNoteRepository(_databasePath);
        await _repository.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NewerRevisionCannotBeOverwrittenByOlderRevision()
    {
        var id = Guid.NewGuid();
        var original = Note(id, "新正文", 2);
        var stale = Note(id, "旧正文", 1);
        Assert.True((await _repository.SaveAsync(original, CancellationToken.None)).Success);

        var result = await _repository.SaveAsync(stale, CancellationToken.None);
        var loaded = await _repository.GetAsync(id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("新正文", loaded?.Body);
        Assert.Equal(2, loaded?.Revision);
    }

    [Fact]
    public async Task DeletedNoteIsIsolatedAndCanBeRestored()
    {
        var note = Note(Guid.NewGuid(), "需要保留", 1);
        await _repository.SaveAsync(note, CancellationToken.None);
        await _repository.SetDeletedAsync(note.Id, true, CancellationToken.None);

        Assert.Empty(await _repository.SearchAsync("", false, CancellationToken.None));
        Assert.Single(await _repository.SearchAsync("", true, CancellationToken.None));
        await _repository.SetDeletedAsync(note.Id, false, CancellationToken.None);
        Assert.Single(await _repository.SearchAsync("", false, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeOnlyRemovesItemsOlderThanCutoff()
    {
        var old = Note(Guid.NewGuid(), "旧条目", 1) with
        {
            DeletedUtc = DateTimeOffset.UtcNow.AddDays(-31)
        };
        var recent = Note(Guid.NewGuid(), "新条目", 1) with
        {
            DeletedUtc = DateTimeOffset.UtcNow.AddDays(-3)
        };
        await _repository.SaveAsync(old, CancellationToken.None);
        await _repository.SaveAsync(recent, CancellationToken.None);
        await _repository.SetDeletedAsync(old.Id, true, CancellationToken.None);
        await _repository.SetDeletedAsync(recent.Id, true, CancellationToken.None);

        await _repository.PurgeDeletedAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            CancellationToken.None);

        Assert.Null(await _repository.GetAsync(old.Id, CancellationToken.None));
        Assert.NotNull(await _repository.GetAsync(recent.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Utf8OneMebibyteLimitCountsEncodedBytes()
    {
        var text = string.Concat(Enumerable.Repeat("中", 349_526));
        var result = await _repository.SaveAsync(
            Note(Guid.NewGuid(), text, 1),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("NoteTooLarge", result.ErrorCode);
    }

    public Task DisposeAsync()
    {
        var root = Path.GetDirectoryName(_databasePath)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        return Task.CompletedTask;
    }

    private static NoteDocument Note(Guid id, string body, long revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new NoteDocument(id, "测试笔记", body, revision, now, now, null);
    }
}
