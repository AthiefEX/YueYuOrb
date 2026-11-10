using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamCompatibilityTests
{
    [Fact]
    public void HostsLeaseRestoresExactBytesAndKeepsOtherEdits()
    {
        var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("# 原有注释\r\n1.2.3.4 example.test")).ToArray();
        var owner = Guid.NewGuid();
        var updated = SteamHostsPatch.Add(original, new(), owner, 123, 456);
        Assert.Contains("127.0.0.1 steamcommunity.com", Encoding.UTF8.GetString(updated));
        Assert.Equal(original, SteamHostsPatch.Remove(updated, owner));
        var otherEdit = Encoding.UTF8.GetBytes("\r\n5.6.7.8 other.test\r\n");
        Assert.Equal(original.Concat(otherEdit), SteamHostsPatch.Remove(updated.Concat(otherEdit).ToArray(), owner));
        Assert.Equal(updated, SteamHostsPatch.Remove(updated, Guid.NewGuid()));
    }

    [Fact]
    public void HostsConflictIsRejectedRatherThanReplacingExistingMapping()
    {
        var original = Encoding.UTF8.GetBytes("1.2.3.4 steamcommunity.com # user entry\r\n");
        Assert.Throws<IOException>(() => SteamHostsPatch.Add(original, new(), Guid.NewGuid(), 123, 456));
    }

    [Fact]
    public void DnsWireValidatesQueryAndDecodesCompressedAddressAnswer()
    {
        var query = SteamDnsMessage.CreateQuery("steamcommunity.com", 1);
        var response = query.ToArray(); response[2] = 0x81; response[3] = 0x80; response[7] = 1;
        response = response.Concat(new byte[] { 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 104, 18, 1, 2 }).ToArray();
        Assert.Equal(IPAddress.Parse("104.18.1.2"), Assert.Single(SteamDnsMessage.ParseAddresses(response, query)));
        response[0] ^= 1;
        Assert.Empty(SteamDnsMessage.ParseAddresses(response, query));
        Assert.Empty(SteamDnsMessage.ParseAddresses(new byte[3], query));
    }

    [Fact]
    public void UpstreamValidationRequiresBothOriginalHostnameAndTrustedChain()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=steamcommunity.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("steamcommunity.com"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.True(SteamUpstreamTls.ValidateCertificate("steamcommunity.com", certificate, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(SteamUpstreamTls.ValidateCertificate("store.steampowered.com", certificate, SslPolicyErrors.None));
        Assert.False(SteamUpstreamTls.ValidateCertificate("steamcommunity.com", certificate, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(SteamUpstreamTls.ValidateCertificate("steamcommunity.com", null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }
}
