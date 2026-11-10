using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

public sealed class SteamTlsBridge : IDisposable, IAsyncDisposable
{
    private readonly SteamCertificateAuthority _certificates;
    private readonly Func<string, CancellationToken, Task<Stream>> _connect;
    private readonly Func<string, CancellationToken, Task<Stream>> _warmConnect;
    private readonly SteamNetworkResolver? _resolver;
    private readonly bool _noSniPreference;
    // Hosts whose last verified connection only completed without SNI. A page opens many
    // connections, and paying the reset plus escalation on every one of them is what made the
    // first page load take ten seconds; the certificate and host name are still verified.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _noSniHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TransferDrainGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TransferIdleCloseThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan NoSniHostLifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<TcpClient, Task> _clientTasks = new();
    private readonly Action<SteamNetworkDiagnostic>? _diagnostic;
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;
    private long _accepted;
    private long _upstreamTlsSuccesses;
    private long _transferredConnections;
    private long _bytesToUpstream;
    private long _bytesToDownstream;
    private long _activeUpstreams;
    private long _failed;
    private string _lastFailure = "无";
    public int Port { get; private set; }
    public long AcceptedConnections => Interlocked.Read(ref _accepted);
    public long UpstreamTlsSuccesses => Interlocked.Read(ref _upstreamTlsSuccesses);
    public long TransferredConnections => Interlocked.Read(ref _transferredConnections);
    public long ActiveUpstreams => Interlocked.Read(ref _activeUpstreams);
    public long BytesToUpstream => Interlocked.Read(ref _bytesToUpstream);
    public long BytesToDownstream => Interlocked.Read(ref _bytesToDownstream);
    public string Counters => $"下游 TLS 接入 {AcceptedConnections}；上游验证成功 {UpstreamTlsSuccesses}；实际传输 {TransferredConnections}；活动上游 {ActiveUpstreams}；双向字节 {BytesToUpstream + BytesToDownstream}；失败请求 {Interlocked.Read(ref _failed)}；最近失败：{_lastFailure}";

    public SteamTlsBridge(SteamCertificateAuthority certificates)
        : this(certificates, new SteamNetworkSettings(), null)
    {
    }

    public SteamTlsBridge(SteamCertificateAuthority certificates, SteamNetworkSettings settings, Action<SteamNetworkDiagnostic>? diagnostic, string? dataDirectory = null)
    {
        _certificates = certificates;
        _diagnostic = diagnostic;
        // In hosts compatibility mode the system resolver is guaranteed to answer with the
        // loopback entry this app just wrote, so it must not be used as a DNS fallback there.
        _resolver = new(dataDirectory is null ? null : Path.Combine(dataDirectory, "steam-network-edges.json"), !settings.UseHostsCompatibility);
        _noSniPreference = settings.UseNoSniCompatibility;
        _connect = (host, token) => ConnectWithMemoryAsync(host, diagnostic, token);
        // Warm-up failures belong in the status line, not in "最近失败": the user has not opened
        // a page yet and the pre-warm is only an optimisation.
        _warmConnect = (host, token) => ConnectWithMemoryAsync(host, null, token);
    }

    internal SteamTlsBridge(SteamCertificateAuthority certificates, Func<string, CancellationToken, Task<Stream>> connect)
    {
        _certificates = certificates;
        _connect = connect;
        _warmConnect = connect;
    }

    private async Task<Stream> ConnectWithMemoryAsync(string host, Action<SteamNetworkDiagnostic>? diagnostic, CancellationToken token)
    {
        var preferNoSni = _noSniHosts.TryGetValue(host, out var until) && until > DateTimeOffset.UtcNow;
        var sawReset = false;
        return await SteamUpstreamTls.ConnectAsync(host, _resolver!, _noSniPreference, Observe, token, null, preferNoSni, Remember)
            .ConfigureAwait(false);

        void Observe(SteamNetworkDiagnostic item)
        {
            // A reset that arrives before any certificate is the path blocking this domain's
            // handshake, not a property of one attempt. Remembering it means the next connection the
            // page opens starts without SNI instead of paying the reset again on every connection.
            if (!preferNoSni && item.Stage == "tls-handshake" && item.ErrorCode == "ConnectionReset")
            {
                sawReset = true;
                _noSniHosts[host] = DateTimeOffset.UtcNow.Add(NoSniHostLifetime);
            }
            diagnostic?.Invoke(item);
        }

        void Remember(bool noSni)
        {
            if (noSni) _noSniHosts[host] = DateTimeOffset.UtcNow.Add(NoSniHostLifetime);
            // A success with the domain handshake only means "SNI works again" when the path did
            // not reset it first; otherwise the reset is the stable fact and stays remembered.
            else if (!sawReset) _noSniHosts.TryRemove(host, out _);
        }
    }

    public void Start(int port = 443)
    {
        if (_listener is not null) throw new InvalidOperationException("TLS 兼容服务已启动。");
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        listener.Start(128); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener; _lifetime = new();
        _acceptTask = AcceptAsync(listener, _lifetime.Token);
    }

    /// <summary>
    /// Opens and closes one verified upstream connection per host so the first real page load
    /// does not pay for the cold resolution walk (measured 1.9–5.7 s on the target network).
    /// The connections are torn down immediately; nothing is cached beyond the resolution hint.
    /// </summary>
    public async Task<string> WarmUpAsync(IReadOnlyList<string> hosts, CancellationToken token)
    {
        var ready = 0;
        string? failure = null;
        foreach (var host in hosts)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                using var stream = await _warmConnect(host, token).ConfigureAwait(false);
                ready++;
            }
            catch (SteamUpstreamConnectException error) { failure ??= error.ErrorCode; }
            catch (OperationCanceledException) { break; }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException) { failure ??= "ConnectFailed"; }
        }
        if (ready == hosts.Count) return "预热完成。";
        if (ready > 0) return "预热部分完成（" + (failure ?? "Timeout") + "）。";
        return "预热未完成（" + (failure ?? "Timeout") + "），首屏可能稍慢。";
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_clients.Count >= 128) { client.Dispose(); continue; }
                _clients.TryAdd(client, 0);
                _clientTasks[client] = RunTrackedAsync(client, token);
            }
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task RunTrackedAsync(TcpClient client, CancellationToken token)
    {
        await Task.Yield();
        try { await ServeAsync(client, token).ConfigureAwait(false); }
        finally
        {
            _clients.TryRemove(client, out _);
            _clientTasks.TryRemove(client, out _);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        var stage = "downstream-tls";
        string? host = null;
        TransferProgress? progress = null;
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        using (var downstream = new SslStream(client.GetStream()))
        {
            try
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                await downstream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { host = name; return _certificates.CertificateFor(name); },
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, deadline.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _accepted);
                stage = "upstream-tls";
                using var upstream = await _connect(host!, deadline.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _upstreamTlsSuccesses);
                Interlocked.Increment(ref _activeUpstreams);
                _lastFailure = "无";
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                stage = "transfer";
                try
                {
                    progress = new TransferProgress();
                    var outbound = CopyAndCountAsync(downstream, upstream, count =>
                        RecordTransferBytes(progress, toUpstream: true, count), deadline.Token);
                    var inbound = CopyAndCountAsync(upstream, downstream, count =>
                        RecordTransferBytes(progress, toUpstream: false, count), deadline.Token);
                    var first = await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
                    if (first.IsFaulted) await first.ConfigureAwait(false);
                    if (ReferenceEquals(first, outbound))
                    {
                        // The downstream side finished first. The upstream may still be holding
                        // response bytes (a client that half-closes, or an early FIN), so drain
                        // inbound for a bounded grace window instead of aborting immediately.
                        using var grace = CancellationTokenSource.CreateLinkedTokenSource(token);
                        grace.CancelAfter(TransferDrainGrace);
                        try { await inbound.WaitAsync(grace.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                    deadline.Cancel();
                    try { await Task.WhenAll(outbound, inbound).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                    catch (IOException) when (progress.ExchangedBytes > 0) { }
                    catch (SocketException) when (progress.ExchangedBytes > 0) { }
                }
                finally { Interlocked.Decrement(ref _activeUpstreams); }
            }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                if (!token.IsCancellationRequested)
                {
                    if (stage == "transfer" && progress is not null &&
                        error is IOException or SocketException &&
                        IsGracefulPeerClose(progress.ExchangedBytes, Stopwatch.GetElapsedTime(progress.LastActivityTicks)))
                        return;

                    Interlocked.Increment(ref _failed);
                    var code = error is SteamUpstreamConnectException connection
                        ? connection.ErrorCode
                        : stage == "downstream-tls" ? "ClientHandshakeFailed"
                        : stage == "transfer" ? "TransferFailed"
                        : error is OperationCanceledException ? "Timeout" : "ConnectFailed";
                    _lastFailure = code;
                    if (error is not SteamUpstreamConnectException)
                    {
                        var safeHost = _certificates.IsSelectedHost(host) ? host! : "unknown";
                        var diagnostic = SteamNetworkDiagnostic.Create(safeHost, null, "domain", stage, timer.ElapsedMilliseconds, code, false);
                        try { _diagnostic?.Invoke(diagnostic); }
                        catch (Exception callbackError) when (callbackError is InvalidOperationException or ObjectDisposedException) { }
                    }
                }
            }
        }
    }

    private sealed class TransferProgress
    {
        internal long BytesToUpstream;
        internal long BytesToDownstream;
        internal int CountedBidirectional;
        internal long LastActivityTicks;

        internal long ExchangedBytes => Interlocked.Read(ref BytesToUpstream) + Interlocked.Read(ref BytesToDownstream);
    }

    /// <summary>
    /// A peer that resets an established connection after it has been idle for a while is closing
    /// it, not failing the request: CEF keeps community and CDN connections open and the edge tears
    /// them down on its own schedule. Counting those as failures made a working page report
    /// "传输失败 30 秒" in the settings window (measured twice in one acceptance run).
    /// </summary>
    internal static bool IsGracefulPeerClose(long exchangedBytes, TimeSpan idle) =>
        exchangedBytes > 0 && idle >= TransferIdleCloseThreshold;

    private void RecordTransferBytes(TransferProgress progress, bool toUpstream, int count)
    {
        Volatile.Write(ref progress.LastActivityTicks, Stopwatch.GetTimestamp());
        if (toUpstream)
        {
            Interlocked.Add(ref _bytesToUpstream, count);
            Interlocked.Add(ref progress.BytesToUpstream, count);
        }
        else
        {
            Interlocked.Add(ref _bytesToDownstream, count);
            Interlocked.Add(ref progress.BytesToDownstream, count);
        }

        if (Interlocked.Read(ref progress.BytesToUpstream) > 0 &&
            Interlocked.Read(ref progress.BytesToDownstream) > 0 &&
            Interlocked.CompareExchange(ref progress.CountedBidirectional, 1, 0) == 0)
            Interlocked.Increment(ref _transferredConnections);
    }

    private static async Task CopyAndCountAsync(Stream source, Stream destination, Action<int> count, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int size;
            while ((size = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
                count(size);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        _listener?.Stop();
        _listener = null;
        foreach (var client in _clients.Keys) client.Dispose();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException) { }
            _acceptTask = null;
        }
        var tasks = _clientTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { }
        }
        _lifetime?.Dispose();
        _lifetime = null;
        _resolver?.Dispose();
    }
}
