using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace FloatDesk.Windows.Network;

internal sealed class SteamBackgroundAuthority : IDisposable
{
    private const string Prefix = "YueYu-Steam-Machine-";
    private static string RecordPath => Path.Combine(SteamBackgroundInstallation.DirectoryPath, "authority.json");
    private sealed record Record(Guid Owner, string KeyName, string Certificate, string Thumbprint);
    private readonly RSACng _key;
    internal X509Certificate2 Certificate { get; }

    internal SteamBackgroundAuthority()
    {
        Record? record = File.Exists(RecordPath) ? JsonSerializer.Deserialize<Record>(File.ReadAllText(RecordPath)) : null;
        if (record is not null)
        {
            Validate(record);
            using var publicOnly = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(record.Certificate));
            if (publicOnly.Thumbprint != record.Thumbprint || publicOnly.Subject != "CN=YueYu Steam Background CA " + record.Owner.ToString("N")) throw new IOException("后台证书归属不匹配。");
            if (publicOnly.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7)) { Uninstall(); record = null; }
        }
        if (record is null)
        {
            var owner = Guid.NewGuid(); var name = Prefix + owner.ToString("N");
            var creation = new CngKeyCreationParameters { Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None, KeyCreationOptions = CngKeyCreationOptions.MachineKey };
            creation.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using var key = CngKey.Create(CngAlgorithm.Rsa, name, creation);
            using var rsa = new RSACng(key);
            var request = new CertificateRequest("CN=YueYu Steam Background CA " + owner.ToString("N"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90));
            record = new(owner, name, Convert.ToBase64String(certificate.RawData), certificate.Thumbprint);
            try
            {
                File.WriteAllText(RecordPath + ".tmp", JsonSerializer.Serialize(record));
                File.Move(RecordPath + ".tmp", RecordPath, true);
            }
            catch { key.Delete(); throw; }
        }
        _key = new RSACng(CngKey.Open(record.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey));
        using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(record.Certificate));
        Certificate = root.CopyWithPrivateKey(_key);
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        using var publicRoot = X509CertificateLoader.LoadCertificate(Certificate.RawData);
        store.Add(publicRoot);
    }

    internal bool IsTrusted()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, Certificate.Thumbprint, false);
        try { return found.Count == 1 && found[0].RawData.AsSpan().SequenceEqual(Certificate.RawData) && Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow; }
        finally { foreach (var certificate in found) certificate.Dispose(); }
    }

    internal X509Certificate2 Sign(string host, string csr) => Sign(Certificate, host, csr);

    internal static X509Certificate2 Sign(X509Certificate2 root, string host, string csr)
    {
        if (!SteamBackgroundProtocol.IsAllowed("SIGN", 0, host) || csr.Length > 8192) throw new IOException("不允许的签发请求。");
        var loaded = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var rsa = loaded.PublicKey.GetRSAPublicKey();
        if (rsa is null || rsa.KeySize is < 2048 or > 4096) throw new IOException("叶公钥参数无效。");
        var request = new CertificateRequest(new X500DistinguishedName("CN=" + host), loaded.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        return request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddDays(2), RandomNumberGenerator.GetBytes(16));
    }

    internal static void Uninstall()
    {
        if (!File.Exists(RecordPath)) return;
        var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(RecordPath)) ?? throw new IOException("证书记录无效。");
        Validate(record);
        using var publicOnly = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(record.Certificate));
        if (publicOnly.Thumbprint != record.Thumbprint || publicOnly.Subject != "CN=YueYu Steam Background CA " + record.Owner.ToString("N")) throw new IOException("证书记录不匹配。");
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, record.Thumbprint, false))
            using (certificate) if (certificate.RawData.AsSpan().SequenceEqual(publicOnly.RawData)) store.Remove(certificate);
        if (CngKey.Exists(record.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey))
            using (var key = CngKey.Open(record.KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey)) key.Delete();
        File.Delete(RecordPath);
    }

    private static void Validate(Record record)
    {
        if (record.KeyName != Prefix + record.Owner.ToString("N") || record.Thumbprint.Length != 40 || !record.Thumbprint.All(char.IsAsciiHexDigit)) throw new IOException("后台证书记录不属于月语。");
    }

    public void Dispose() { Certificate.Dispose(); _key.Dispose(); }
}
