using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamDnsProviderTests
{
    [Fact]
    public async Task CommunityUsesIndependentHttpsResolversInsteadOfObservedIncorrectAnswers()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (request, token) =>
        {
            var type = Type(request);
            if (request.RequestUri!.Host == "doh.pub") return Answer(type, "23.221.132.220");
            await Task.Delay(300, token);
            return Answer(type, "54.234.18.200");
        }));

        var addresses = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);

        // The independent answer comes first; a later provider answer may still be kept as an
        // additional candidate, but it must never displace the independent one.
        Assert.Equal(IPAddress.Parse("23.221.132.220"), addresses[0]);
        Assert.True(addresses.Length >= 1);
    }

    [Fact]
    public async Task InvalidatingHostForcesAFreshQueryInsteadOfServingTheCachedAnswer()
    {
        var answer = "157.240.9.36";
        using var resolver = new SteamNetworkResolver(
            (_, _, _) => Task.FromResult(new[] { IPAddress.Parse(answer) }),
            (_, _) => Task.FromResult(Array.Empty<IPAddress>()),
            allowSystemFallback: false);

        var first = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
        Assert.Equal(IPAddress.Parse("157.240.9.36"), first[0]);

        answer = "23.63.77.13";
        var cached = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
        Assert.Equal(IPAddress.Parse("157.240.9.36"), cached[0]);

        // A failed connect must not keep poisoning the next page load for the rest of the cache
        // lifetime: the entry is dropped so the retry can see a genuine answer.
        resolver.Invalidate("steamcommunity.com");
        var refreshed = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
        Assert.Equal(IPAddress.Parse("23.63.77.13"), refreshed[0]);
    }

    [Fact]
    public async Task SlowProviderDoesNotBlockOtherProviderOrDiscardValidIpv4()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (request, token) =>
        {
            var type = Type(request);
            if (request.RequestUri!.Host != "doh.360.cn") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Answer(type, "23.221.132.220");
        }));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var addresses = await resolver.ResolveAsync("steamcommunity.com", true, deadline.Token);

        Assert.Equal(IPAddress.Parse("23.221.132.220"), addresses[0]);
    }

    [Fact]
    public async Task UnusableFirstProviderAnswerDoesNotSuppressUsableSecondProvider()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (request, token) =>
        {
            var type = Type(request);
            if (request.RequestUri!.Host == "doh.pub") return Answer(type, "127.0.0.1");
            if (request.RequestUri.Host == "doh.360.cn") await Task.Delay(25, token);
            else await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Answer(type, "23.221.132.220");
        }));

        var addresses = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("23.221.132.220"), Assert.Single(addresses));
    }

    [Fact]
    public async Task CancellingLookupWaitsForAllProviderRequestsToRelease()
    {
        var active = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (_, token) =>
        {
            Interlocked.Increment(ref active);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(HttpStatusCode.OK); }
            finally { Interlocked.Decrement(ref active); }
        }));
        using var cancellation = new CancellationTokenSource();
        var lookup = resolver.ResolveAsync("steamcommunity.com", true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task ProviderFailuresAreDistinguishableAndNeverCarryRawText()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "doh.pub")
                throw new HttpRequestException("https://doh.pub/dns-query?name=x&token=secret", new AuthenticationException("inner"));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }), allowSystemFallback: false);
        var reported = new List<SteamNetworkDiagnostic>();

        var addresses = await resolver.ResolveCandidatesAsync(
            "steamcommunity.com", true, CancellationToken.None, reported.Add);

        Assert.Empty(addresses);
        var diagnostic = Assert.Single(reported);
        Assert.Equal("DnsUnreachable", diagnostic.ErrorCode);
        Assert.Contains("DNSPod:TlsFailed", diagnostic.Detail);
        Assert.Contains("Timeout", diagnostic.Detail);
        Assert.DoesNotContain("secret", diagnostic.Detail);
    }

    [Fact]
    public async Task JsonAnswerForTheWrongFamilyIsRejected()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler(async (request, _) =>
        {
            await Task.CompletedTask;
            // The A question has no answer, and the AAAA question is answered with an IPv4 record.
            return Type(request) == "AAAA"
                ? Raw("{\"Status\":0,\"Answer\":[{\"type\":1,\"data\":\"23.221.132.220\"}]}")
                : Raw("{\"Status\":0}");
        }), allowSystemFallback: false);
        var reported = new List<SteamNetworkDiagnostic>();

        var addresses = await resolver.ResolveCandidatesAsync(
            "steamcommunity.com", true, CancellationToken.None, reported.Add);

        Assert.Empty(addresses);
        Assert.Equal("DnsNoAnswer", Assert.Single(reported).ErrorCode);
    }

    [Fact]
    public async Task Ipv6IsOnlyAskedWhenIpv4HasNoAnswer()
    {
        var families = new List<string>();
        using var resolver = new SteamNetworkResolver(new DnsHandler((request, _) =>
        {
            families.Add(Type(request));
            return Task.FromResult(Answer(Type(request), "23.221.132.220"));
        }));

        var addresses = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);

        // Measured: every AAAA answer for these names was an injected address that cost a six
        // second connect timeout, so the family is not even asked for while IPv4 answers exist.
        Assert.NotEmpty(families);
        Assert.DoesNotContain("AAAA", families);
        Assert.Equal(IPAddress.Parse("23.221.132.220"), Assert.Single(addresses));
    }

    [Fact]
    public async Task Ipv6IsUsedWhenIpv4HasNoAnswer()
    {
        using var resolver = new SteamNetworkResolver(new DnsHandler((request, _) =>
        {
            if (Type(request) == "AAAA")
                return Task.FromResult(Raw("{\"Status\":0,\"Answer\":[{\"type\":28,\"data\":\"2606:4700::6810:1\"}]}"));
            return Task.FromResult(Raw("{\"Status\":0}"));
        }), allowSystemFallback: false);

        var addresses = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);

        Assert.Equal(IPAddress.Parse("2606:4700::6810:1"), Assert.Single(addresses));
    }

    [Fact]
    public async Task RememberedEdgeStaysInFrontWhenTheAnswerOnlyCarriesPoisonedAddresses()
    {
        using var resolver = new SteamNetworkResolver(Alias(), allowSystemFallback: false);
        var first = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
        Assert.Equal(IPAddress.Parse(Poisoned), first[0]);

        // A verified handshake for an address that the answer does not even mention any more must
        // still be tried first: the edge is checked again on every attempt.
        resolver.Prefer("steamcommunity.com", IPAddress.Parse(Verified));

        var again = await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
        Assert.Equal(IPAddress.Parse(Verified), again[0]);
    }

    [Fact]
    public async Task RememberedEdgeSurvivesRestartAndAnInvalidatedAnswer()
    {
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Edges-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var resolver = new SteamNetworkResolver(Alias(), false, path))
            {
                await resolver.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
                resolver.Prefer("steamcommunity.com", IPAddress.Parse(Verified));
            }

            using (var restarted = new SteamNetworkResolver(Alias(), false, path))
            {
                var addresses = await restarted.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
                Assert.Equal(IPAddress.Parse(Verified), addresses[0]);
                restarted.Invalidate("steamcommunity.com");
            }

            using (var afterFailure = new SteamNetworkResolver(Alias(), false, path))
            {
                var addresses = await afterFailure.ResolveAsync("steamcommunity.com", true, CancellationToken.None);
                // Dropping the cached answer must not drop the edge that already completed a
                // verified handshake: measured in one session the answers rotated to poisoned
                // addresses while that edge was the only routable one left. Its own lifetime is what
                // retires it.
                Assert.Equal(IPAddress.Parse(Verified), addresses[0]);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RememberedEdgeIsUsedWhenResolutionReturnsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Edges-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var resolver = new SteamNetworkResolver(Empty(), false, path))
                resolver.Prefer("steamcommunity.com", IPAddress.Parse(Verified));

            using (var restarted = new SteamNetworkResolver(Empty(), false, path))
                Assert.Equal(IPAddress.Parse(Verified), Assert.Single(await restarted.ResolveAsync("steamcommunity.com", true, CancellationToken.None)));
        }
        finally { File.Delete(path); }
    }

    private const string Poisoned = "157.240.9.36";
    private const string Verified = "23.15.140.216";

    private static DnsHandler Alias() => new((request, _) => Task.FromResult(Answer(Type(request), Poisoned)));

    private static DnsHandler Empty() => new((_, _) => Task.FromResult(Raw("{\"Status\":0}")));

    private static string Type(HttpRequestMessage request) =>
        request.RequestUri!.Query.Contains("type=AAAA", StringComparison.Ordinal) ? "AAAA" : "A";

    private static HttpResponseMessage Raw(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/dns-json") };

    private static HttpResponseMessage Answer(string type, string address)
    {
        var record = type == "AAAA" ? 28 : 1;
        if (record != 1) return new(HttpStatusCode.OK) { Content = new StringContent("{\"Status\":0}", Encoding.UTF8) };
        var json = "{\"Status\":0,\"Answer\":[{\"name\":\"steamcommunity.com.\",\"type\":1,\"TTL\":60,\"data\":\"" + address + "\"}]}";
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/dns-json") };
    }

    private sealed class DnsHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
