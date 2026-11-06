using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.DesktopSessions;

namespace FloatDesk.Infrastructure.Tests;

public sealed class SessionExecutionLedgerTests
{
    [Fact]
    public async Task StartupExecutionIsRecordedOnceAcrossReload()
    {
        var directory = CreateDirectory();
        try
        {
            var key = CreateKey();
            using (var ledger = new SessionExecutionLedger(directory))
            {
                Assert.True(await ledger.TryBeginAsync(
                    key,
                    CancellationToken.None));
                Assert.False(await ledger.TryBeginAsync(
                    key,
                    CancellationToken.None));
            }

            using var reloaded = new SessionExecutionLedger(directory);
            Assert.False(await reloaded.TryBeginAsync(
                key,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptPrimaryUsesBackupSnapshot()
    {
        var directory = CreateDirectory();
        try
        {
            var key = CreateKey();
            using (var ledger = new SessionExecutionLedger(directory))
            {
                Assert.True(await ledger.TryBeginAsync(
                    key,
                    CancellationToken.None));
                Assert.True(await ledger.TryBeginAsync(
                    CreateKey(entryRevision: 2),
                    CancellationToken.None));
            }

            await File.WriteAllTextAsync(
                Path.Combine(directory, "session-executions.json"),
                "{broken");

            using var recovered = new SessionExecutionLedger(directory);
            Assert.False(await recovered.TryBeginAsync(
                key,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"YueYuOrb-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static StartupExecutionKey CreateKey(
        int entryRevision = 1) =>
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            entryRevision);
}
