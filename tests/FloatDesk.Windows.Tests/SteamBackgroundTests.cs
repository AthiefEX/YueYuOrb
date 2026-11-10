using FloatDesk.Windows.Network;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.IO.Pipes;
using System.Security.Principal;

namespace FloatDesk.Windows.Tests;

public sealed class SteamBackgroundTests
{
    [Fact]
    public void TemporaryHostsSharingViolationIsRetriedButOtherErrorsAreNot()
    {
        var calls = 0; var delays = 0;
        SteamBackgroundServer.RetryHostUpdate(() =>
        {
            if (++calls < 4) throw new IOException("sharing", unchecked((int)0x80070020));
        }, _ => delays++);
        Assert.Equal(4, calls);
        Assert.Equal(3, delays);
        var permanentCalls = 0;
        Assert.Throws<IOException>(() => SteamBackgroundServer.RetryHostUpdate(() =>
        { permanentCalls++; throw new IOException("permanent"); }, _ => throw new InvalidOperationException()));
        Assert.Equal(1, permanentCalls);
    }

    [Fact]
    public void PersistentHostsSharingViolationRemainsBounded()
    {
        var calls = 0;
        Assert.Throws<IOException>(() => SteamBackgroundServer.RetryHostUpdate(() =>
        { calls++; throw new IOException("sharing", unchecked((int)0x80070020)); }, _ => { }));
        Assert.Equal(10, calls);
    }
    [Fact]
    public void CallerSidComesFromTheActualClientProcessToken()
    {
        using var current = WindowsIdentity.GetCurrent();
        Assert.True(SteamBackgroundInstallation.ClientSidMatches(Environment.ProcessId, current.User!.Value));
        Assert.False(SteamBackgroundInstallation.ClientSidMatches(Environment.ProcessId, "S-1-5-21-0-0-0-99999"));
    }
    [Theory]
    [InlineData("STATUS", 0, null, true)]
    [InlineData("LEASE", 7, null, true)]
    [InlineData("LEASE", 8, null, false)]
    [InlineData("LEASE", -1, null, false)]
    [InlineData("SIGN", 0, "steamcommunity.com", true)]
    [InlineData("SIGN", 0, "steamcommunity.com.evil.test", false)]
    [InlineData("EXEC", 0, null, false)]
    public void RequestsAreFixedPurpose(string command, int mask, string? host, bool expected) =>
        Assert.Equal(expected, SteamBackgroundProtocol.IsAllowed(command, mask, host));

    [Fact]
    public void ResponseDoesNotCarryPrivateKeyMaterial()
    {
        var properties = typeof(SteamBackgroundReply).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(properties, name => name.Contains("Private", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OversizedMessagesAreRejectedBeforeDeserialization()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(new string('x', SteamBackgroundProtocol.MaximumMessageLength + 1) + "\n"));
        using var reader = new StreamReader(input);
        await Assert.ThrowsAsync<IOException>(() => SteamBackgroundClient.ReadMessageAsync(reader, CancellationToken.None));
    }

    [Fact]
    public void RuntimeRecoveryPreservesAuthorityAndClearsOnlySessionKeys()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYu-RootReuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = Path.Combine(directory, "trust.json");
        try
        {
            string thumbprint;
            using (var first = new SteamCertificateAuthority(new() { Store = false, Content = false }, journal))
            {
                thumbprint = first.RootCertificate.Thumbprint;
                using var before = JsonDocument.Parse(File.ReadAllText(journal));
                var authority = before.RootElement.GetProperty("AuthorityKey").GetString()!;
                var leaves = before.RootElement.GetProperty("Keys").EnumerateArray().Select(value => value.GetString()!).ToArray();

                SteamCertificateAuthority.RecoverSession(journal);

                Assert.True(CngKey.Exists(authority, CngProvider.MicrosoftSoftwareKeyStorageProvider));
                Assert.All(leaves, name => Assert.False(CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)));
            }
            using var second = new SteamCertificateAuthority(new() { Store = false, Content = false }, journal);
            Assert.Equal(thumbprint, second.RootCertificate.Thumbprint);
        }
        finally { SteamCertificateAuthority.RecoverTrust(journal); Directory.Delete(directory, true); }
    }

    [Fact]
    public void SigningIgnoresCallerSubjectAlternativeNamesAndCaExtensions()
    {
        using var key = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Test Root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(5));
        using var leafKey = RSA.Create(2048);
        var malicious = new CertificateRequest("CN=evil.test", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("evil.test");
        malicious.CertificateExtensions.Add(san.Build());
        malicious.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var leaf = SteamBackgroundAuthority.Sign(root, "steamcommunity.com", malicious.CreateSigningRequestPem());

        Assert.True(leaf.MatchesHostname("steamcommunity.com"));
        Assert.False(leaf.MatchesHostname("evil.test"));
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.False(leaf.HasPrivateKey);
        Assert.Throws<IOException>(() => SteamBackgroundAuthority.Sign(root, "evil.test", malicious.CreateSigningRequestPem()));
    }
}
