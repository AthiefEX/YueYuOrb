using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamProxyLeaseTests
{
    [Fact]
    public void DormantPacIsPreservedWithoutBlockingAcceleration()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var original = new ProxyConfiguration(1, "https://example.test/inactive.pac");
        var store = new MemoryProxy { Current = original };
        var lease = new SteamProxyLease(path, store);
        try
        {
            lease.Acquire(new Uri("http://127.0.0.1:12345/test.pac"));
            lease.Restore();
            Assert.Equal(original, store.Current);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void StopRestoresOriginalSettingsAndDeletesJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new MemoryProxy();
        var original = store.Current;
        var lease = new SteamProxyLease(path, store);
        lease.Acquire(new Uri("http://127.0.0.1:12345/test.pac"));
        Assert.True(File.Exists(path));
        Assert.Equal(5, store.Current.Flags);
        lease.Restore();
        Assert.Equal(original, store.Current);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void ExistingProxyIsNeverReplaced(int flags)
    {
        var store = new MemoryProxy { Current = new(flags, "") };
        var lease = new SteamProxyLease(Path.GetTempPath() + Guid.NewGuid() + ".json", store);
        Assert.Throws<InvalidOperationException>(() => lease.Acquire(new Uri("http://127.0.0.1:12345/test.pac")));
        Assert.Equal(flags, store.Current.Flags);
    }

    [Fact]
    public void FailedRestoreRetainsJournalForRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new MemoryProxy();
        var lease = new SteamProxyLease(path, store);
        lease.Acquire(new Uri("http://127.0.0.1:12345/test.pac"));
        store.FailWrites = true;
        Assert.Throws<IOException>(() => lease.Restore());
        Assert.True(File.Exists(path));
        store.FailWrites = false;
        lease.Restore();
        Assert.Equal(new ProxyConfiguration(1, ""), store.Current);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RestartRestoresOrphanedLeaseButPreservesExternalChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new MemoryProxy();
        new SteamProxyLease(path, store).Acquire(new Uri("http://127.0.0.1:12345/test.pac"));
        new SteamProxyLease(path, store).Restore();
        Assert.Equal(new ProxyConfiguration(1, ""), store.Current);
        new SteamProxyLease(path, store).Acquire(new Uri("http://127.0.0.1:12345/second.pac"));
        store.Current = new(5, "https://example.test/company.pac");
        new SteamProxyLease(path, store).Restore();
        Assert.Equal("https://example.test/company.pac", store.Current.AutoConfigUrl);
        Assert.False(File.Exists(path));
    }

    private sealed class MemoryProxy : IProxyConfigurationStore
    {
        public ProxyConfiguration Current { get; set; } = new(1, "");
        public bool FailWrites { get; set; }
        public ProxyConfiguration Read() => Current;
        public void Write(ProxyConfiguration configuration)
        {
            if (FailWrites) throw new IOException("Simulated configuration write failure.");
            Current = configuration;
        }
    }
}
