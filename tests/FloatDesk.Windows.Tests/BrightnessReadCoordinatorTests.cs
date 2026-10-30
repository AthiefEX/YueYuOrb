using FloatDesk.Core.Models;
using FloatDesk.Windows.Brightness;

namespace FloatDesk.Windows.Tests;

public sealed class BrightnessReadCoordinatorTests
{
    [Fact]
    public async Task HungDiscoveryIsIsolatedAndNeverSpawnsDuplicateWorkers()
    {
        var reader = new BrightnessReadCoordinator();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var timeout = TimeSpan.FromMilliseconds(80);
        BrightnessDevice Blocked() { Interlocked.Increment(ref calls); release.Wait(); return new("a", "A", 20, null, false); }
        try
        {
            var a = reader.ReadAsync("a", 0, Blocked, timeout);
            var b = await reader.ReadAsync("b", 0, () => new("b", "B", 80, null, false), timeout);
            Assert.Equal(80, b.Percent);
            Assert.NotNull((await a).UnavailableReason);
            Assert.NotNull((await reader.ReadAsync("a", 0, Blocked, timeout)).UnavailableReason);
            Assert.Equal(1, calls);
            Assert.Null((await reader.ReadAsync("a", 1, Blocked, timeout)).Percent);
        }
        finally { release.Set(); }
    }
}
