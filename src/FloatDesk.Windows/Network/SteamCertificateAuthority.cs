using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

/// <summary>
/// Issues the local leaf certificates the TLS bridge presents to Steam.
///
/// The authority is kept in the current user's protected key store and reused across accelerator
/// starts, so Windows asks for confirmation once instead of on every start (install) and stop
/// (delete). Only leaf keys are per session and they are removed as soon as acceleration stops;
/// the root stays trusted because removing it is what triggers the second confirmation dialog.
/// It is removed by the explicit recovery action, when it is close to expiry, or when the record
/// does not belong to this product.
/// </summary>
public sealed class SteamCertificateAuthority : IDisposable
{
    private const string SubjectPrefix = "CN=YueYu Steam Local CA ";
    private const string AuthorityKeyPrefix = "YueYu-Steam-CA-";
    private const string LeafKeyPrefix = "YueYu-Steam-TLS-";
    private const string LengthProperty = "Length";
    private static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(90);
    // A near-expiry authority is replaced on the next start rather than in the middle of a page.
    private static readonly TimeSpan AuthorityRenewBefore = TimeSpan.FromDays(7);
    private static readonly CngProvider KeyProvider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
    private readonly Dictionary<string, X509Certificate2> _leaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _journal;
    private readonly bool _persistent;
    private readonly bool _background;
    private RSACng? _authorityKey;
    private string? _authorityKeyName;
    private string[] _leafKeys = [];
    private Guid _owner = Guid.NewGuid();

    internal X509Certificate2 RootCertificate { get; private set; } = null!;

    private sealed record TrustRecord(
        string Thumbprint,
        string Subject,
        Guid Owner,
        string[] Keys,
        string? AuthorityKey = null,
        string? AuthorityCertificate = null,
        long? NotAfterUtcTicks = null,
        bool Background = false);

    /// <param name="journalPath">
    /// Stable record path used by the application. When it is null the authority is disposable
    /// only (tests and read-only probes), which is why nothing is written to the user profile.
    /// </param>
    public SteamCertificateAuthority(SteamNetworkSettings settings, string? journalPath = null)
    {
        _journal = journalPath;
        _persistent = journalPath is not null;
        var reused = _persistent && TryLoadAuthority(journalPath!);
        if (_persistent && !reused) DiscardUnusableRecord(journalPath!);
        if (!reused) CreateAuthority();
        BeginSession(settings);
    }

    internal SteamCertificateAuthority(SteamNetworkSettings settings, string journalPath, X509Certificate2 backgroundRoot)
    {
        _journal = journalPath;
        _background = true;
        RootCertificate = X509CertificateLoader.LoadCertificate(backgroundRoot.RawData);
        BeginSession(settings);
    }

    public X509Certificate2 CertificateFor(string? host) => host is not null && _leaves.TryGetValue(host, out var certificate)
        ? certificate : throw new AuthenticationException("TLS 目标不在已选择的 Steam 站点范围内。");

    internal bool IsSelectedHost(string? host) => host is not null && _leaves.ContainsKey(host);

    /// <summary>
    /// Installs the root in the current user's trusted store unless the same certificate is
    /// already there. The second call therefore does not raise the Windows security warning.
    /// </summary>
    /// <returns>True when the certificate had to be added.</returns>
    public bool EnsureTrust()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (IsInstalled(store)) return false;
        using var publicCertificate = X509CertificateLoader.LoadCertificate(RootCertificate.RawData);
        store.Add(publicCertificate);
        return true;
    }

    internal bool IsTrusted()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return IsInstalled(store);
    }

    /// <summary>Rebuilds the per-session leaves. Previous leaf keys are always removed first.</summary>
    public void BeginSession(SteamNetworkSettings settings)
    {
        EndSession();
        var domains = SteamNetworkPolicy.Domains(settings);
        var run = Guid.NewGuid().ToString("N");
        _leafKeys = domains
            .Select((_, index) => LeafKeyPrefix + _owner.ToString("N") + "-" + run + "-" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        // Record the intended keys before creating them: an interrupted session then leaves behind
        // key files that the next start knows about by name and removes.
        SaveRecord();
        try
        {
            for (var index = 0; index < domains.Length; index++)
            {
                var host = domains[index];
                var creation = new CngKeyCreationParameters { Provider = KeyProvider, ExportPolicy = CngExportPolicies.None };
                creation.Parameters.Add(new CngProperty(LengthProperty, BitConverter.GetBytes(2048), CngPropertyOptions.None));
                using var container = CngKey.Create(CngAlgorithm.Rsa, _leafKeys[index], creation);
                using var key = new RSACng(container);
                var leafRequest = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName(host);
                leafRequest.CertificateExtensions.Add(san.Build());
                leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                using var leaf = _background
                    ? SteamBackgroundClient.Sign(host, leafRequest.CreateSigningRequestPem(), RootCertificate.Thumbprint)
                    : leafRequest.Create(RootCertificate, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(16));
                if (!leaf.MatchesHostname(host) || leaf.Issuer != RootCertificate.Subject) throw new CryptographicException("后台叶证书不匹配。");
                _leaves.Add(host, leaf.CopyWithPrivateKey(key));
            }
        }
        catch
        {
            EndSession();
            throw;
        }
    }

    /// <summary>Removes the leaf certificates and their keys, keeping the trusted authority.</summary>
    public void EndSession()
    {
        foreach (var certificate in _leaves.Values) certificate.Dispose();
        _leaves.Clear();
        foreach (var name in _leafKeys) TryDeleteKey(name);
        _leafKeys = [];
        SaveRecord();
    }

    /// <summary>Removes everything this product installed for the given record path.</summary>
    public static void RecoverTrust(string journal)
    {
        if (!File.Exists(journal)) return;
        var record = JsonSerializer.Deserialize<TrustRecord>(File.ReadAllText(journal)) ?? throw new IOException("证书恢复记录无效。");
        if (!IsOwnedRecord(record)) throw new IOException("证书恢复记录不属于月语，未作修改。");
        using (var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, record.Thumbprint, false))
            {
                using (certificate)
                    if (certificate.Subject == record.Subject) store.Remove(certificate);
            }
        }
        foreach (var name in record.Keys) TryDeleteKey(name);
        if (record.AuthorityKey is not null) TryDeleteKey(record.AuthorityKey);
        File.Delete(journal);
    }

    public static void RecoverSession(string journal)
    {
        var record = ReadRecord(journal);
        if (record is null) return;
        if (!IsOwnedRecord(record)) throw new IOException("证书恢复记录不属于月语。");
        foreach (var name in record.Keys) TryDeleteKey(name);
        File.WriteAllText(journal + ".tmp", JsonSerializer.Serialize(record with { Keys = [] }));
        File.Move(journal + ".tmp", journal, true);
    }

    public void Dispose()
    {
        try { EndSession(); }
        finally
        {
            _authorityKey?.Dispose();
            _authorityKey = null;
            RootCertificate.Dispose();
            // Only the disposable mode owns the authority key; the application keeps it so the
            // next start does not have to ask Windows to trust a new root certificate again.
            if (!_persistent && _authorityKeyName is not null) TryDeleteKey(_authorityKeyName);
        }
    }

    private bool TryLoadAuthority(string journal)
    {
        TrustRecord? record;
        try { record = ReadRecord(journal); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
        if (record is null || record.AuthorityKey is null || record.AuthorityCertificate is null) return false;
        if (!IsOwnedRecord(record)) return false;
        if (!record.AuthorityKey.StartsWith(AuthorityKeyPrefix + record.Owner.ToString("N") + "-", StringComparison.Ordinal)) return false;
        if (!CngKey.Exists(record.AuthorityKey, KeyProvider)) return false;
        byte[] raw;
        try { raw = Convert.FromBase64String(record.AuthorityCertificate); }
        catch (FormatException) { return false; }
        X509Certificate2 loaded;
        try { loaded = X509CertificateLoader.LoadCertificate(raw); }
        catch (CryptographicException) { return false; }
        try
        {
            if (loaded.Thumbprint != record.Thumbprint || loaded.Subject != record.Subject) return false;
            if (loaded.NotAfter.ToUniversalTime() <= DateTime.UtcNow + AuthorityRenewBefore) return false;
            _authorityKey = new RSACng(CngKey.Open(record.AuthorityKey, KeyProvider));
            RootCertificate = loaded.CopyWithPrivateKey(_authorityKey);
            _authorityKeyName = record.AuthorityKey;
            _owner = record.Owner;
            // Keys recorded by a session that never ended are leftovers from an interrupted run.
            foreach (var name in record.Keys) TryDeleteKey(name);
            return true;
        }
        catch (CryptographicException)
        {
            _authorityKey?.Dispose();
            _authorityKey = null;
            return false;
        }
        finally { loaded.Dispose(); }
    }

    private void CreateAuthority()
    {
        _authorityKeyName = AuthorityKeyPrefix + _owner.ToString("N") + "-" + Guid.NewGuid().ToString("N");
        var creation = new CngKeyCreationParameters { Provider = KeyProvider, ExportPolicy = CngExportPolicies.None };
        creation.Parameters.Add(new CngProperty(LengthProperty, BitConverter.GetBytes(2048), CngPropertyOptions.None));
        X509Certificate2 publicOnly;
        using (var container = CngKey.Create(CngAlgorithm.Rsa, _authorityKeyName, creation))
        using (var key = new RSACng(container))
        {
            var request = new CertificateRequest(SubjectPrefix + _owner.ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.Add(AuthorityLifetime));
            publicOnly = X509CertificateLoader.LoadCertificate(selfSigned.RawData);
        }
        // Re-open the persisted container: the creation handle is closed above so the key stays
        // available to the next process without ever being exportable.
        _authorityKey = new RSACng(CngKey.Open(_authorityKeyName, KeyProvider));
        RootCertificate = publicOnly.CopyWithPrivateKey(_authorityKey);
        publicOnly.Dispose();
    }

    private void SaveRecord()
    {
        if (_journal is null || (!_background && (!_persistent || _authorityKeyName is null))) return;
        try
        {
            var record = new TrustRecord(
                RootCertificate.Thumbprint,
                RootCertificate.Subject,
                _owner,
                _leafKeys,
                _authorityKeyName,
                Convert.ToBase64String(RootCertificate.RawData),
                RootCertificate.NotAfter.ToUniversalTime().Ticks,
                _background);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_journal))!);
            File.WriteAllText(_journal + ".tmp", JsonSerializer.Serialize(record));
            File.Move(_journal + ".tmp", _journal, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            if (_background) throw new IOException("无法保存临时叶密钥恢复记录。", error);
            // Losing the record only costs one extra confirmation next time; never fail the start.
        }
    }

    private static TrustRecord? ReadRecord(string journal)
    {
        if (!File.Exists(journal)) return null;
        return JsonSerializer.Deserialize<TrustRecord>(File.ReadAllText(journal));
    }

    /// <summary>
    /// Drops a record that can no longer be reused. A record that belongs to this product is
    /// cleaned up fully; anything unreadable or foreign is moved aside untouched so a corrupted
    /// file cannot block the accelerator forever.
    /// </summary>
    private static void DiscardUnusableRecord(string journal)
    {
        try { RecoverTrust(journal); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or CryptographicException)
        {
            try { File.Move(journal, journal + ".unreadable", true); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsOwnedRecord(TrustRecord record)
    {
        if (record.Background)
        {
            const string prefix = "CN=YueYu Steam Background CA ";
            if (!record.Subject.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(record.Subject[prefix.Length..], "N", out _) || record.AuthorityKey is not null) return false;
        }
        else if (record.Subject != SubjectPrefix + record.Owner.ToString("N")) return false;
        if (record.Thumbprint.Length != 40 || !record.Thumbprint.All(char.IsAsciiHexDigit)) return false;
        if (record.Keys is null || record.Keys.Length > 64) return false;
        var leafPrefix = LeafKeyPrefix + record.Owner.ToString("N") + "-";
        return record.Keys.All(key => key.StartsWith(leafPrefix, StringComparison.Ordinal));
    }

    private bool IsInstalled(X509Store store)
    {
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, RootCertificate.Thumbprint, false))
        {
            using (certificate)
                if (certificate.Subject == RootCertificate.Subject) return true;
        }
        return false;
    }

    private static void TryDeleteKey(string name)
    {
        try
        {
            if (!CngKey.Exists(name, KeyProvider)) return;
            using var key = CngKey.Open(name, KeyProvider);
            key.Delete();
        }
        catch (CryptographicException) { }
    }
}
