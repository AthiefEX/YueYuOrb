using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class ChildSessionAgentBootstrapStoreTests
{
    [Fact]
    public void BootstrapIsBoundToExactGenerationAndConsumedOnce()
    {
        using var store = new ChildSessionAgentBootstrapStore();
        var handle = new SessionHandle(
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            Guid.NewGuid(),
            true);
        var secret = Enumerable.Range(0, 32)
            .Select(value => (byte)value)
            .ToArray();
        store.Set(
            handle,
            new ChildSessionAgentBootstrap(
                handle.LoginInstanceId,
                "S-1-5-21-1",
                42,
                DateTimeOffset.UtcNow,
                secret));

        var stale = handle with { Generation = Guid.NewGuid() };
        Assert.Throws<InvalidOperationException>(() => store.Take(stale));
        var bootstrap = store.Take(handle);

        Assert.Equal(handle.LoginInstanceId, bootstrap.LoginInstanceId);
        Assert.Equal(secret, bootstrap.BootstrapSecret);
        Assert.Throws<InvalidOperationException>(() => store.Take(handle));
    }
}
