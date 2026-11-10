using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamNetworkTests
{
    [Theory]
    [InlineData("steamcommunity.com", true)]
    [InlineData("STORE.STEAMPOWERED.COM", true)]
    [InlineData("community.steamstatic.com", true)]
    [InlineData("steamcommunity.com.evil.test", false)]
    [InlineData("evilsteamcommunity.com", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    public void OnlySelectedOfficialDomainsAreAllowed(string host, bool allowed) =>
        Assert.Equal(allowed, SteamNetworkPolicy.IsAllowed(host, new()));

    [Fact]
    public void DisablingCommunityAlsoDisablesWorkshopDomain() =>
        Assert.False(SteamNetworkPolicy.IsAllowed("steamcommunity.com", new() { Community = false }));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("2001:2::35")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.4")]
    public void PrivateAddressesCannotBeDialed(string address) =>
        Assert.False(SteamNetworkPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    public void GloballyRoutableAddressesRemainEligible(string address) =>
        Assert.True(SteamNetworkPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task ResolverUsesQualifiedHttpsDnsWithoutMergingSystemResults()
    {
        var systemQueries = 0;
        using var resolver = new SteamNetworkResolver(
            (_, type, _) => Task.FromResult(type == 1 ? new[] { IPAddress.Parse("8.8.8.8") } : Array.Empty<IPAddress>()),
            (_, _) => { systemQueries++; return Task.FromResult(new[] { IPAddress.Parse("9.9.9.9") }); });

        var result = await resolver.ResolveCandidatesAsync("steamcommunity.com", true, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(result).Address);
        Assert.Equal(SteamDnsSource.HttpsDns, result[0].Source);
        Assert.Equal(0, systemQueries);
    }

    [Fact]
    public async Task ResolverFallsBackToSystemOnlyWhenHttpsResultsAreFilteredOut()
    {
        var systemQueries = 0;
        using var resolver = new SteamNetworkResolver(
            (_, type, _) => Task.FromResult(type == 28 ? new[] { IPAddress.Parse("2001:2::35") } : Array.Empty<IPAddress>()),
            (_, _) => { systemQueries++; return Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }); });

        var result = await resolver.ResolveCandidatesAsync("steamcommunity.com", true, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(result).Address);
        Assert.Equal(SteamDnsSource.SystemDns, result[0].Source);
        Assert.Equal(1, systemQueries);
    }

    [Fact]
    public async Task ResolverKeepsHttpsDnsTimeoutAndSystemFallbackInsideFourSeconds()
    {
        using var resolver = new SteamNetworkResolver(
            async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            },
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("1.1.1.1") }));
        var timer = Stopwatch.StartNew();

        var result = await resolver.ResolveCandidatesAsync("steamcommunity.com", true, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(result).Address);
        Assert.Equal(SteamDnsSource.SystemDns, result[0].Source);
        Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(4500), $"DNS took {timer.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public void PacOnlyRoutesSelectedDomainsAndRetainsDirectFallback()
    {
        var pac = SteamNetworkPolicy.BuildPac(new() { Store = false }, 12345);
        Assert.Contains("steamcommunity.com", pac);
        Assert.DoesNotContain("store.steampowered.com", pac);
        Assert.Contains("PROXY 127.0.0.1:12345; DIRECT", pac);
        Assert.Contains("=== 'https:'", pac);
    }

    [Fact]
    public async Task RealLoopbackServiceServesPacAndRejectsOpenProxyRequests()
    {
        using var service = new SteamNetworkService();
        service.Start(new());
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        Assert.Contains("FindProxyForURL", await http.GetStringAsync(service.PacUri));
        Assert.Contains("403", await RequestAsync(service.Port, "CONNECT example.com:443 HTTP/1.1\r\n\r\n"));
        Assert.Contains("403", await RequestAsync(service.Port, "CONNECT steamcommunity.com:22 HTTP/1.1\r\n\r\n"));
        Assert.Contains("403", await RequestAsync(service.Port, "GET http://steamcommunity.com/ HTTP/1.1\r\n\r\n"));
        Assert.Contains("403", await RequestAsync(service.Port, new string('a', 8192)));
        service.Stop();
        Assert.False(service.IsRunning);
    }

    [Theory]
    [InlineData(1024, 3, true)]
    [InlineData(1024, 0, false)]
    [InlineData(0, 30, false)]
    public void IdlePeerCloseIsNotCountedAsTransferFailure(long exchangedBytes, int idleSeconds, bool expected) =>
        Assert.Equal(expected, SteamTlsBridge.IsGracefulPeerClose(exchangedBytes, TimeSpan.FromSeconds(idleSeconds)));

    private static async Task<string> RequestAsync(int port, string request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
        var buffer = new byte[2048];
        var size = await tcp.GetStream().ReadAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer, 0, size);
    }
}
