using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

/// <summary>Restricted loopback CONNECT tunnel; TLS remains end to end.</summary>
public sealed class SteamNetworkService : IDisposable
{
    internal static readonly TimeSpan OuterRequestTimeout = TimeSpan.FromSeconds(35);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly SteamNetworkResolver _resolver;
    private readonly Func<AddressFamily, TcpClient> _createCandidateClient;
    private readonly Func<TcpClient, IPAddress, CancellationToken, Task> _connectCandidate;
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private long _tunnelEstablished;
    private long _tunnelTransferred;
    private long _tunnelBytesDownstream;
    private readonly string _pacPath = "/" + Guid.NewGuid().ToString("N") + ".pac";
    public int Port { get; private set; }
    public bool IsRunning => _listener is not null;
    public long TunnelConnections => Interlocked.Read(ref _tunnelEstablished);
    public Uri PacUri => new($"http://127.0.0.1:{Port}{_pacPath}");
    public event Action<SteamNetworkDiagnostic>? Diagnostic;

    public SteamNetworkService()
        : this(new SteamNetworkResolver(), family => new TcpClient(family),
            (client, address, token) => client.ConnectAsync(address, 443, token).AsTask())
    {
    }

    internal SteamNetworkService(
        SteamNetworkResolver resolver,
        Func<AddressFamily, TcpClient> createCandidateClient,
        Func<TcpClient, IPAddress, CancellationToken, Task> connectCandidate)
    {
        _resolver = resolver;
        _createCandidateClient = createCandidateClient;
        _connectCandidate = connectCandidate;
    }

    public void Start(SteamNetworkSettings settings)
    {
        if (IsRunning) throw new InvalidOperationException("请先停止加速再修改设置。");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(32);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _lifetime = new();
        _ = AcceptAsync(listener, settings, _lifetime.Token);
    }

    private async Task AcceptAsync(TcpListener listener, SteamNetworkSettings settings, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_clients.Count >= 32) { client.Dispose(); continue; }
                _clients.TryAdd(client, 0);
                _ = HandleAsync(client, settings, token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, SteamNetworkSettings settings, CancellationToken token)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var established = false;
            try
            {
                deadline.CancelAfter(OuterRequestTimeout);
                var stream = client.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (header.Length < 8192)
                {
                    if (await stream.ReadAsync(one, deadline.Token).ConfigureAwait(false) == 0) return;
                    header.Append((char)one[0]);
                    if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                }
                var line = header.ToString().Split("\r\n", StringSplitOptions.None)[0].Split(' ');
                if (line.Length == 3 && line[0] == "GET" && line[1] == _pacPath)
                {
                    await ReplyAsync(stream, "200 OK", "application/x-ns-proxy-autoconfig", SteamNetworkPolicy.BuildPac(settings, Port), deadline.Token).ConfigureAwait(false);
                    return;
                }
                if (header.Length >= 8192 || line.Length != 3 || line[0] != "CONNECT" ||
                    !line[1].EndsWith(":443", StringComparison.Ordinal) || !SteamNetworkPolicy.IsAllowed(line[1][..^4], settings))
                {
                    await ReplyAsync(stream, "403 Forbidden", "text/plain", "Only selected Steam HTTPS domains are supported.", deadline.Token).ConfigureAwait(false);
                    return;
                }
                using var upstream = await ConnectAsync(line[1][..^4], settings, deadline.Token).ConfigureAwait(false);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
                established = true;
                Interlocked.Increment(ref _tunnelEstablished);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                var remote = upstream.GetStream();
                var up = 0L;
                var down = 0L;
                var counted = 0;
                var outbound = CopyAsync(stream, remote, size => Interlocked.Add(ref up, size), deadline.Token);
                var inbound = CopyAsync(remote, stream, size =>
                {
                    Interlocked.Add(ref down, size);
                    Interlocked.Add(ref _tunnelBytesDownstream, size);
                    if (Volatile.Read(ref up) > 0 && Interlocked.CompareExchange(ref counted, 1, 0) == 0)
                        Interlocked.Increment(ref _tunnelTransferred);
                }, deadline.Token);
                var first = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
                if (first.IsFaulted) await first.ConfigureAwait(false);
                if (ReferenceEquals(first, outbound))
                {
                    // Downstream finished first: let the upstream response drain before aborting,
                    // otherwise a client that half-closes truncates the page it asked for.
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(token);
                    grace.CancelAfter(TimeSpan.FromSeconds(4));
                    try { await inbound.WaitAsync(grace.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                deadline.Cancel();
                try { await Task.WhenAll(outbound, inbound).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!established && !token.IsCancellationRequested)
                    try { await ReplyAsync(client.GetStream(), "502 Bad Gateway", "text/plain", "Upstream connection failed.", token).ConfigureAwait(false); }
                    catch (Exception sendError) when (sendError is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
            }
            finally { _clients.TryRemove(client, out _); }
        }
    }

    private async Task<TcpClient> ConnectAsync(string host, SteamNetworkSettings settings, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var candidates = await _resolver.ResolveCandidatesAsync(host, settings.UseHttpsDns, total.Token, Report).ConfigureAwait(false);
            foreach (var candidate in candidates.Take(6))
            {
                total.Token.ThrowIfCancellationRequested();
                var connection = await TryConnectCandidateAsync(
                    host,
                    candidate,
                    _createCandidateClient,
                    _connectCandidate,
                    Report,
                    TimeSpan.FromSeconds(4),
                    total.Token).ConfigureAwait(false);
                if (connection is not null) return connection;
            }
            throw new IOException("Steam 上游 TCP 连接失败。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Report(SteamNetworkDiagnostic.Create(host, null, "none", "budget", timer.ElapsedMilliseconds, "TotalTimeout", false));
            throw new IOException("Steam 上游连接超过总时限。");
        }
    }

    internal static async Task<TcpClient?> TryConnectCandidateAsync(
        string host,
        SteamResolvedAddress candidate,
        Func<AddressFamily, TcpClient> createClient,
        Func<TcpClient, IPAddress, CancellationToken, Task> connect,
        Action<SteamNetworkDiagnostic> report,
        TimeSpan attemptTimeout,
        CancellationToken totalToken)
    {
        var timer = Stopwatch.StartNew();
        var connection = createClient(candidate.Address.AddressFamily);
        var returned = false;
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(totalToken);
            attempt.CancelAfter(attemptTimeout);
            await connect(connection, candidate.Address, attempt.Token).ConfigureAwait(false);
            report(SteamNetworkDiagnostic.Create(host, candidate, "domain", "tcp-connect", timer.ElapsedMilliseconds, "None", false));
            returned = true;
            return connection;
        }
        catch (OperationCanceledException) when (!totalToken.IsCancellationRequested)
        {
            report(SteamNetworkDiagnostic.Create(host, candidate, "domain", "tcp-connect", timer.ElapsedMilliseconds, "Timeout", false));
            return null;
        }
        catch (SocketException error)
        {
            var reset = error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            report(SteamNetworkDiagnostic.Create(host, candidate, "domain", "tcp-connect", timer.ElapsedMilliseconds, reset ? "ConnectionReset" : "ConnectFailed", false));
            return null;
        }
        finally
        {
            if (!returned) connection.Dispose();
        }
    }

    private void Report(SteamNetworkDiagnostic diagnostic)
    {
        try { Diagnostic?.Invoke(diagnostic); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    private static async Task CopyAsync(NetworkStream source, NetworkStream destination, Action<int> counted, CancellationToken token)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int size;
            while ((size = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
                counted(size);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static Task ReplyAsync(NetworkStream stream, string status, string type, string body, CancellationToken token) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{body}"), token).AsTask();

    public async Task<string> DiagnoseAsync(SteamNetworkSettings settings, bool throughTunnel, CancellationToken token) =>
        SteamCompatibilityDiagnostics.Format(await DiagnoseDetailedAsync(settings, throughTunnel, token).ConfigureAwait(false));

    /// <summary>
    /// PAC-mode probe. TLS stays end to end here, so the upstream certificate is the real one and
    /// the result proves only what the tunnel carried, never that the client path is accelerated.
    /// </summary>
    public async Task<IReadOnlyList<SteamNetworkProbeResult>> DiagnoseDetailedAsync(SteamNetworkSettings settings, bool throughTunnel, CancellationToken token)
    {
        var targets = SteamCompatibilityDiagnostics.Targets(settings);
        if (targets.Count == 0) return [];
        var before = TunnelConnections;
        var results = await Task.WhenAll(targets.Select(async (url, index) =>
        {
            var target = new Uri(url);
            var timer = Stopwatch.StartNew();
            using var handler = new HttpClientHandler
            {
                UseProxy = throughTunnel,
                Proxy = throughTunnel ? new WebProxy($"http://127.0.0.1:{Port}") : null,
                AllowAutoRedirect = false
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            try
            {
                using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                return new SteamNetworkProbeResult(
                    "probe-" + index,
                    throughTunnel ? SteamProbeRoute.LocalBridge : SteamProbeRoute.SystemDirect,
                    target.Host,
                    "http",
                    status,
                    throughTunnel && TunnelConnections > before,
                    status < 400 ? "None" : "Http" + status,
                    timer.ElapsedMilliseconds);
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                return new SteamNetworkProbeResult(
                    "probe-" + index,
                    throughTunnel ? SteamProbeRoute.LocalBridge : SteamProbeRoute.SystemDirect,
                    target.Host,
                    "connect",
                    null,
                    throughTunnel && TunnelConnections > before,
                    error is OperationCanceledException ? "Timeout" : "ConnectFailed",
                    timer.ElapsedMilliseconds);
            }
        })).ConfigureAwait(false);
        return results;
    }

    public void Stop()
    {
        _lifetime?.Cancel();
        _listener?.Stop();
        _listener = null;
        foreach (var client in _clients.Keys) client.Dispose();
        _lifetime?.Dispose();
        _lifetime = null;
    }
    public void Dispose() { Stop(); _resolver.Dispose(); }
}
