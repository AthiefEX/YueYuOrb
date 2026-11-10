using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace FloatDesk.Windows.Network;

/// <summary>
/// Independent HTTPS-DNS resolvers for the selected Steam hosts.
/// Providers are raced in parallel; the first usable answer wins per record family,
/// and an unusable answer from one provider never suppresses a usable answer from another.
/// </summary>
public sealed class SteamNetworkResolver : IDisposable
{
    private sealed record DnsEndpoint(string Name, Uri Uri);

    // Ordered by measured reachability on the target networks. Providers that are blocked
    // (for example Cloudflare/Google over some mainland links) fail fast without delaying a
    // provider that already produced a usable answer.
    //
    // The JSON API is used on purpose: measured on the target network, DNSPod answered
    // "23.15.140.216" (a routable edge) for the JSON form of the same question while the wire
    // form returned unrelated edges that were unreachable. Both forms stay on the provider's own
    // origin and every answer is still validated against the original host name upstream.
    private static readonly DnsEndpoint[] DnsEndpoints =
    [
        new("DNSPod", new("https://doh.pub/dns-query")),
        new("360", new("https://doh.360.cn/resolve")),
        new("Cloudflare", new("https://cloudflare-dns.com/dns-query")),
        new("Google", new("https://dns.google/resolve"))
    ];

    private static readonly TimeSpan HttpsBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DnsBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CollectionWindow = TimeSpan.FromMilliseconds(700);
    // A verified edge stays a candidate for this long. Filtered answers rotate every few minutes,
    // so a remembered edge outlives one cache window but never becomes a permanent assumption.
    private static readonly TimeSpan PreferredLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PreferredWriteInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<(string Host, bool Https), (DateTimeOffset Expires, SteamResolvedAddress[] Addresses)> _cache = new();
    // Address that completed a verified handshake for a host. Filtered answers rotate constantly,
    // so reusing the edge that already worked is the difference between a 1 s page load and a
    // 20 s walk through blackholed answers.
    private readonly ConcurrentDictionary<string, (IPAddress Address, DateTimeOffset Used)> _preferred = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _preferencePath;
    private readonly object _preferenceGate = new();
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue;
    private readonly HttpClient _dns;
    private readonly Func<string, ushort, CancellationToken, Task<IPAddress[]>>? _customHttps;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _systemQuery;
    private readonly bool _allowSystemFallback;

    public SteamNetworkResolver() : this(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, true) { }

    internal SteamNetworkResolver(string? preferencePath, bool allowSystemFallback)
        : this(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, allowSystemFallback, preferencePath) { }

    internal SteamNetworkResolver(bool allowSystemFallback) : this(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, allowSystemFallback) { }

    internal SteamNetworkResolver(HttpMessageHandler handler, bool allowSystemFallback = true, string? preferencePath = null)
    {
        _dns = new(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 65535 };
        _systemQuery = ResolveSystemAsync;
        _allowSystemFallback = allowSystemFallback;
        _preferencePath = preferencePath;
        LoadPreferences();
    }

    internal SteamNetworkResolver(
        Func<string, ushort, CancellationToken, Task<IPAddress[]>> httpsQuery,
        Func<string, CancellationToken, Task<IPAddress[]>> systemQuery,
        bool allowSystemFallback = true) : this(allowSystemFallback)
    {
        _customHttps = httpsQuery;
        _systemQuery = systemQuery;
    }

    public async Task<IPAddress[]> ResolveAsync(string host, bool httpsDns, CancellationToken cancellationToken)
    {
        var candidates = await ResolveCandidatesAsync(host, httpsDns, cancellationToken).ConfigureAwait(false);
        return candidates.Select(candidate => candidate.Address).ToArray();
    }

    internal async Task<SteamResolvedAddress[]> ResolveCandidatesAsync(
        string host,
        bool httpsDns,
        CancellationToken cancellationToken,
        Action<SteamNetworkDiagnostic>? diagnostic = null)
    {
        if (_cache.TryGetValue((host, httpsDns), out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            // A successful handshake can land after this entry was cached; promote on read so the
            // verified edge is used by the next page load instead of the poisoned list.
            return Promote(host, cached.Addresses);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        using var dnsBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        dnsBudget.CancelAfter(DnsBudget);
        var failures = new ConcurrentQueue<string>();
        SteamResolvedAddress[] candidates = [];
        var source = "none";
        var provider = "none";
        var attemptedHttps = false;
        var attemptedSystem = false;
        var httpsAnswered = false;

        if (httpsDns && !dnsBudget.IsCancellationRequested)
        {
            attemptedHttps = true;
            using var httpsBudget = CancellationTokenSource.CreateLinkedTokenSource(dnsBudget.Token);
            httpsBudget.CancelAfter(HttpsBudget);
            try
            {
                var lookup = await ResolveHttpsCandidatesAsync(host, failures, diagnostic, httpsBudget.Token).ConfigureAwait(false);
                candidates = lookup.Addresses;
                provider = lookup.Provider;
                httpsAnswered = lookup.Answered;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A bounded HTTPS-DNS timeout permits the remaining DNS budget for system fallback.
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Length > 0)
            {
                source = "https-dns";
                return Cache(host, httpsDns, candidates);
            }
        }

        if (_allowSystemFallback && !dnsBudget.IsCancellationRequested)
        {
            attemptedSystem = true;
            try
            {
                var system = Filter(await _systemQuery(host, dnsBudget.Token).ConfigureAwait(false), SteamDnsSource.SystemDns);
                if (system.Length > 0)
                {
                    source = "system-dns";
                    provider = "system";
                    candidates = system;
                }
                else
                {
                    failures.Enqueue("system:HostsLoopback");
                }
            }
            catch (SocketException) { failures.Enqueue("system:ResolveFailed"); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // DNS budget expired; report a bounded empty result below.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (candidates.Length == 0 && TryPreferred(host, out var remembered))
        {
            // Nothing usable came back this time. An edge that already completed a verified
            // handshake is a better bet than an empty result; it is still validated in full on
            // every attempt, so a remembered address can never bypass the certificate checks.
            return Cache(host, httpsDns, [remembered]);
        }
        if (candidates.Length > 0) return Cache(host, httpsDns, candidates);

        source = attemptedHttps && attemptedSystem ? "https-dns+system-dns" : attemptedHttps ? "https-dns" : attemptedSystem ? "system-dns" : "none";
        var code = !attemptedHttps ? "NoPublicAddress"
            : httpsAnswered ? "DnsNoAnswer"
            : IsTransportOnly(failures) ? "DnsUnreachable"
            : "NoPublicAddress";
        var detail = string.Join(',', failures.Take(8));
        diagnostic?.Invoke(SteamNetworkDiagnostic.Create(host, null, "none", "dns", timer.ElapsedMilliseconds, code, false) with
        {
            Source = source,
            Provider = provider,
            Detail = detail
        });
        return [];
    }

    private static bool IsTransportOnly(ConcurrentQueue<string> failures)
    {
        if (failures.IsEmpty) return false;
        return failures.All(failure =>
            failure.EndsWith(":Timeout", StringComparison.Ordinal) ||
            failure.EndsWith(":TlsFailed", StringComparison.Ordinal) ||
            failure.EndsWith(":ConnectFailed", StringComparison.Ordinal) ||
            failure.EndsWith(":HttpFailed", StringComparison.Ordinal));
    }

    private static SteamResolvedAddress[] Filter(IEnumerable<IPAddress> addresses, SteamDnsSource source) => addresses
        .Where(SteamNetworkPolicy.IsPublicAddress)
        .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
        .Distinct()
        .Select(address => new SteamResolvedAddress(address, source))
        .ToArray();

    /// <summary>
    /// Drops the cached answer for a host so the next attempt asks again. A filtered network
    /// returns a mixture of real and blackholed answers for the same name, and which one arrives
    /// changes between queries. The remembered edge survives on purpose: measured in one session,
    /// the answers rotated to poisoned addresses twice while the edge that had just completed two
    /// verified handshakes was the only routable one left. Its own 10 minute lifetime is what
    /// retires it, not a single failed round.
    /// </summary>
    internal void Invalidate(string host)
    {
        _cache.TryRemove((host, true), out _);
        _cache.TryRemove((host, false), out _);
    }

    /// <summary>
    /// Records the edge that just completed a verified handshake for this host so that the next
    /// lookup puts it first. Nothing is trusted on the strength of this record: the address still
    /// has to pass a full certificate chain and original-host-name check on every attempt.
    /// </summary>
    internal void Prefer(string host, IPAddress address)
    {
        if (_preferred.Count >= 128) _preferred.Clear();
        _preferred[host] = (address, DateTimeOffset.UtcNow);
        SavePreferences();
    }

    private SteamResolvedAddress[] Cache(string host, bool httpsDns, SteamResolvedAddress[] candidates)
    {
        if (_cache.Count >= 64) _cache.Clear();
        var ordered = Promote(host, candidates);
        _cache[(host, httpsDns)] = (DateTimeOffset.UtcNow.AddSeconds(30), ordered);
        return ordered;
    }

    private SteamResolvedAddress[] Promote(string host, SteamResolvedAddress[] candidates)
    {
        if (!TryPreferred(host, out var remembered)) return candidates;
        var index = Array.FindIndex(candidates, candidate => candidate.Address.Equals(remembered.Address));
        if (index == 0) return candidates;
        if (index < 0)
        {
            // The current answer only carries blackholed addresses. Keep the verified edge in
            // front instead of walking a list that is known to be poisoned; the address still
            // has to pass the same chain and original-host-name validation on this attempt.
            var extended = new SteamResolvedAddress[candidates.Length + 1];
            extended[0] = remembered;
            candidates.CopyTo(extended, 1);
            return extended;
        }
        var ordered = new SteamResolvedAddress[candidates.Length];
        ordered[0] = candidates[index];
        var position = 1;
        for (var i = 0; i < candidates.Length; i++)
            if (i != index) ordered[position++] = candidates[i];
        return ordered;
    }

    private bool TryPreferred(string host, out SteamResolvedAddress remembered)
    {
        if (_preferred.TryGetValue(host, out var preferred))
        {
            if (DateTimeOffset.UtcNow - preferred.Used <= PreferredLifetime)
            {
                remembered = new(preferred.Address, SteamDnsSource.HttpsDns);
                return true;
            }
            // An edge that stops answering must not stay first forever.
            _preferred.TryRemove(host, out _);
        }
        remembered = null!;
        return false;
    }

    private void LoadPreferences()
    {
        if (_preferencePath is null || !File.Exists(_preferencePath)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, StoredEdge>>(File.ReadAllText(_preferencePath));
            if (stored is null) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var (host, edge) in stored.Take(256))
            {
                if (!IPAddress.TryParse(edge.Address, out var address) || !SteamNetworkPolicy.IsPublicAddress(address)) continue;
                if (edge.UsedUnixMilliseconds <= 0) continue;
                var used = DateTimeOffset.FromUnixTimeMilliseconds(edge.UsedUnixMilliseconds);
                if (used > now || now - used > PreferredLifetime) continue;
                _preferred[host] = (address, used);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private void SavePreferences(bool force = false)
    {
        if (_preferencePath is null) return;
        lock (_preferenceGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastWrite < PreferredWriteInterval) return;
            _lastWrite = now;
            try
            {
                var snapshot = _preferred
                    .Where(pair => now - pair.Value.Used <= PreferredLifetime)
                    .Take(128)
                    .ToDictionary(pair => pair.Key, pair => new StoredEdge(pair.Value.Address.ToString(), pair.Value.Used.ToUnixTimeMilliseconds()));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_preferencePath))!);
                File.WriteAllText(_preferencePath + ".tmp", JsonSerializer.Serialize(snapshot));
                File.Move(_preferencePath + ".tmp", _preferencePath, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record StoredEdge(string Address, long UsedUnixMilliseconds);

    private static Task<IPAddress[]> ResolveSystemAsync(string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token);

    internal sealed record SteamHttpsLookup(SteamResolvedAddress[] Addresses, string Provider, bool Answered);

    /// <summary>
    /// Asks for IPv4 first and only falls back to AAAA when there is no IPv4 answer at all.
    /// Measured on the target network, every AAAA answer for the selected names was an injected
    /// address (<c>2a03:2880::/32</c> with a <c>face:b00c</c> tail) and each one cost a six second
    /// connect timeout, so carrying them beside a working IPv4 edge only delayed the page.
    /// </summary>
    internal async Task<SteamHttpsLookup> ResolveHttpsCandidatesAsync(
        string host,
        ConcurrentQueue<string>? failures,
        Action<SteamNetworkDiagnostic>? diagnostic,
        CancellationToken token)
    {
        var observed = failures ?? new ConcurrentQueue<string>();
        var primary = await QueryFamilyAsync(host, 1, observed, diagnostic, token).ConfigureAwait(false);
        if (primary.Addresses.Length > 0) return primary;
        var fallback = await QueryFamilyAsync(host, 28, observed, diagnostic, token).ConfigureAwait(false);
        if (fallback.Addresses.Length > 0) return fallback;
        return new([], "none", primary.Answered || fallback.Answered);
    }

    private async Task<SteamHttpsLookup> QueryFamilyAsync(
        string host,
        ushort type,
        ConcurrentQueue<string> failures,
        Action<SteamNetworkDiagnostic>? diagnostic,
        CancellationToken token)
    {
        if (_customHttps is not null)
        {
            IPAddress[] addresses;
            try { addresses = await _customHttps(host, type, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is HttpRequestException or SocketException or IOException) { addresses = []; }
            var filtered = Filter(addresses, SteamDnsSource.HttpsDns);
            if (filtered.Length == 0) failures.Enqueue("custom:NoAnswer");
            return new(filtered, "custom", true);
        }

        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = DnsEndpoints.Select((endpoint, index) =>
            (Index: index, endpoint.Name, Task: QueryEndpointAsync(endpoint, host, type, lookup.Token))).ToList();
        var found = new List<(int Index, string Provider, SteamResolvedAddress[] Addresses)>();
        var answered = false;
        var collecting = false;
        try
        {
            while (pending.Count > 0 && !lookup.IsCancellationRequested)
            {
                var finished = await Task.WhenAny(pending.Select(item => item.Task)).ConfigureAwait(false);
                var index = pending.FindIndex(item => ReferenceEquals(item.Task, finished));
                var provider = pending[index].Name;
                var order = pending[index].Index;
                pending.RemoveAt(index);
                var (addresses, error) = await finished.ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    if (!collecting)
                    {
                        // Do not stop at the first provider: answers are geo-steered, and a second
                        // provider may hold an edge that is actually routable from this network.
                        collecting = true;
                        lookup.CancelAfter(CollectionWindow);
                    }
                    found.Add((order, provider, Filter(addresses, SteamDnsSource.HttpsDns)));
                    continue;
                }
                failures.Enqueue(provider + ":" + error);
                if (error is not ("Timeout" or "TlsFailed" or "ConnectFailed" or "HttpFailed")) answered = true;
            }
        }
        catch (OperationCanceledException) when (collecting && !token.IsCancellationRequested)
        {
            // The collection window closed; report what has already been gathered.
        }
        finally
        {
            lookup.Cancel();
            foreach (var item in pending)
            {
                try { await item.Task.ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // Only the shared budget expiring counts as a provider timeout; a cancellation
                    // raised because the collection window closed says nothing about this provider.
                    if (token.IsCancellationRequested) failures.Enqueue(item.Name + ":Timeout");
                }
            }
        }

        if (found.Count == 0) return new([], "none", answered);
        var ordered = found.OrderBy(entry => entry.Index).SelectMany(entry => entry.Addresses)
            .DistinctBy(candidate => candidate.Address).ToArray();
        var winner = found.OrderBy(entry => entry.Index).First();
        diagnostic?.Invoke(SteamNetworkDiagnostic.Create(host, null, "domain", "dns", 0, "None", false) with
        {
            Source = "https-dns",
            Provider = winner.Provider,
            RecordType = type == 1 ? "A" : "AAAA"
        });
        return new(ordered, winner.Provider, true);
    }

    /// <summary>Returns parsed addresses plus a controlled error code; never exposes raw exception text.</summary>
    private async Task<(IPAddress[] Addresses, string ErrorCode)> QueryEndpointAsync(DnsEndpoint endpoint, string host, ushort type, CancellationToken token)
    {
        try
        {
            var query = $"?name={Uri.EscapeDataString(host)}&type={(type == 1 ? "A" : "AAAA")}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint.Uri.AbsoluteUri + query));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
            using var response = await _dns.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return ([], "Http" + (int)response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            return ParseJson(bytes, type);
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return ([], "Timeout");
        }
        catch (HttpRequestException error)
        {
            return ([], Classify(error));
        }
        catch (SocketException) { return ([], "ConnectFailed"); }
        catch (IOException) { return ([], "ConnectFailed"); }
    }

    /// <summary>Reads a DoH JSON answer, keeping only records of the requested family.</summary>
    private static (IPAddress[] Addresses, string ErrorCode) ParseJson(byte[] payload, ushort type)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return ([], "ParseRejected");
            if (root.TryGetProperty("Status", out var status) && status.TryGetInt32(out var rcode) && rcode != 0) return ([], "NoAnswer");
            if (!root.TryGetProperty("Answer", out var answers) || answers.ValueKind != System.Text.Json.JsonValueKind.Array) return ([], "NoAnswer");
            var addresses = new List<IPAddress>();
            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!answer.TryGetProperty("type", out var recordType) || !recordType.TryGetInt32(out var record) || record != type) continue;
                if (!answer.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                var text = data.GetString();
                if (text is null || !IPAddress.TryParse(text, out var address)) continue;
                if (type == 1 && address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (type == 28 && address.AddressFamily != AddressFamily.InterNetworkV6) continue;
                if (SteamNetworkPolicy.IsPublicAddress(address)) addresses.Add(address);
            }
            return addresses.Count == 0 ? ([], "NoAnswer") : (addresses.ToArray(), "None");
        }
        catch (System.Text.Json.JsonException) { return ([], "ParseRejected"); }
        catch (ArgumentException) { return ([], "ParseRejected"); }
    }

    private static string Classify(HttpRequestException error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException) return "TlsFailed";
            if (current is SocketException) return "ConnectFailed";
        }
        return "HttpFailed";
    }

    public void Dispose()
    {
        SavePreferences(force: true);
        _dns.Dispose();
    }
}
