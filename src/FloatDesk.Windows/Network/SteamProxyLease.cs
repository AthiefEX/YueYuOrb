using System.Text.Json;

namespace FloatDesk.Windows.Network;

public sealed record ProxyConfiguration(int Flags, string AutoConfigUrl);
public interface IProxyConfigurationStore
{
    ProxyConfiguration Read();
    void Write(ProxyConfiguration configuration);
}

/// <summary>Write-ahead recovery journal. Never restores over another application's configuration.</summary>
public sealed class SteamProxyLease(string journalPath, IProxyConfigurationStore store)
{
    private sealed record Journal(ProxyConfiguration Original, ProxyConfiguration Owned);
    public bool HasRecovery => File.Exists(journalPath);

    public void Acquire(Uri pac)
    {
        if (pac.Scheme != "http" || pac.Host != "127.0.0.1" || pac.Port < 1)
            throw new ArgumentException("PAC must be served on loopback.", nameof(pac));
        Restore();
        var original = store.Read();
        if ((original.Flags & 6) != 0)
            throw new InvalidOperationException("检测到已有代理或自动配置。请先自行关闭已有代理，再启用月语定向加速。");
        var owned = new ProxyConfiguration(5, pac.AbsoluteUri);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        var temporary = journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Journal(original, owned)));
        File.Move(temporary, journalPath, true);
        try { store.Write(owned); }
        catch { Restore(); throw; }
    }

    public void Restore()
    {
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath))
            ?? throw new IOException("网络恢复记录无效，请保留记录并检查系统代理设置。");
        var current = store.Read();
        if (current == journal.Owned) store.Write(journal.Original);
        File.Delete(journalPath);
    }
}
