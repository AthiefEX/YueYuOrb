using FloatDesk.Infrastructure.DesktopSessions;

namespace FloatDesk.Infrastructure.Tests;

public sealed class DesktopTaskExecutionLedgerTests
{
    [Fact]
    public async Task ScheduleKeySurvivesReload()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"YueYuOrb-task-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            const string key =
                "scheduled:env:task:revision:time-zone:2026-11-01T01:30";
            using (var ledger = new DesktopTaskExecutionLedger(directory))
            {
                Assert.True(await ledger.TryBeginAsync(
                    key,
                    CancellationToken.None));
            }

            using var reloaded =
                new DesktopTaskExecutionLedger(directory);
            Assert.False(await reloaded.TryBeginAsync(
                key,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
