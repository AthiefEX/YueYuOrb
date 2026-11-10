using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamUpstreamLoopbackTlsTests
{
    [Theory]
    [InlineData("wrong-host")]
    [InlineData("untrusted-chain")]
    public async Task LoopbackTlsRejectsWrongHostAndUntrustedChain(string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var trustedAuthority = new SteamCertificateAuthority(new SteamNetworkSettings { Store = false, Content = false });
        using var otherAuthority = new SteamCertificateAuthority(new SteamNetworkSettings { Store = false, Content = false });
        X509Certificate2 serverCertificate;
        string originalHost;
        var chainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chainPolicy.CustomTrustStore.Add(trustedAuthority.RootCertificate);

        switch (failure)
        {
            case "wrong-host":
                serverCertificate = trustedAuthority.CertificateFor("steamcommunity.com");
                originalHost = "store.steampowered.com";
                break;
            case "untrusted-chain":
                serverCertificate = otherAuthority.CertificateFor("steamcommunity.com");
                originalHost = "steamcommunity.com";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            try
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(peer.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, timeout.Token);
            }
            catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }, timeout.Token);

        try
        {
            var candidate = new SteamResolvedAddress(IPAddress.Loopback, SteamDnsSource.HttpsDns);
            var error = await Assert.ThrowsAsync<SteamUpstreamAttemptException>(() => SteamUpstreamTls.OpenCandidateAsync(
                originalHost, candidate, false, port, chainPolicy, timeout.Token));

            Assert.Equal("CertificateRejected", error.ErrorCode);
            Assert.False(error.AllowsNoSniRetry);
            await server.WaitAsync(timeout.Token);
        }
        finally
        {
            listener.Stop();
        }
    }
}
