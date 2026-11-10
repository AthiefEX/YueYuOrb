using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamTlsBridgeTests
{
    [Fact]
    public void LeafKeyJournalSupportsCleanupAfterInterruptedLifetime()
    {
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Key-Recovery-" + Guid.NewGuid() + ".json");
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false }, path);
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var names = record.RootElement.GetProperty("Keys").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
        Assert.All(names, name => Assert.True(System.Security.Cryptography.CngKey.Exists(name, System.Security.Cryptography.CngProvider.MicrosoftSoftwareKeyStorageProvider)));
        SteamCertificateAuthority.RecoverTrust(path);
        Assert.False(File.Exists(path));
        Assert.All(names, name => Assert.False(System.Security.Cryptography.CngKey.Exists(name, System.Security.Cryptography.CngProvider.MicrosoftSoftwareKeyStorageProvider)));
    }

    [Fact]
    public void AuthorityIsReusedAcrossSessionsSoTheRootIsConfirmedOnlyOnce()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "steam-network-trust.json");
        var settings = new SteamNetworkSettings { Store = false, Content = false };
        string firstThumbprint;
        using (var authority = new SteamCertificateAuthority(settings, path))
        {
            firstThumbprint = authority.RootCertificate.Thumbprint;
            Assert.False(authority.IsTrusted());
        }

        // A second start must keep issuing leaves from the same root: reinstalling a new root is
        // exactly what makes Windows ask for confirmation on every start.
        using (var authority = new SteamCertificateAuthority(settings, path))
        {
            Assert.Equal(firstThumbprint, authority.RootCertificate.Thumbprint);
            Assert.True(authority.CertificateFor("steamcommunity.com").MatchesHostname("steamcommunity.com"));
        }

        SteamCertificateAuthority.RecoverTrust(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LeafKeysAreRotatedPerSessionAndRemovedWhenItEnds()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "steam-network-trust.json");
        var settings = new SteamNetworkSettings { Store = false, Content = false };
        string[] first;
        string[] second = [];
        using (var authority = new SteamCertificateAuthority(settings, path))
        {
            first = RecordedKeys(path);
            Assert.NotEmpty(first);
            Assert.All(first, name => Assert.True(CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)));

            authority.EndSession();
            Assert.All(first, name => Assert.False(CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)));
            Assert.Throws<System.Security.Authentication.AuthenticationException>(() => authority.CertificateFor("steamcommunity.com"));

            authority.BeginSession(settings);
            second = RecordedKeys(path);
            Assert.NotEqual(first, second);
        }

        Assert.All(second, name => Assert.False(CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)));
        SteamCertificateAuthority.RecoverTrust(path);
    }

    [Fact]
    public void KeysRecordedByAnInterruptedSessionAreRemovedOnTheNextStart()
    {
        using var scope = new TemporaryDirectory();
        var path = Path.Combine(scope.Path, "steam-network-trust.json");
        var settings = new SteamNetworkSettings { Store = false, Content = false };
        var interrupted = new SteamCertificateAuthority(settings, path);
        try
        {
            var stale = RecordedKeys(path);
            Assert.NotEmpty(stale);

            // Simulates the next launch after a crash: the record still lists the leaf keys of the
            // session that never ended.
            using var restarted = new SteamCertificateAuthority(settings, path);
            Assert.All(stale, name => Assert.False(CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider)));
        }
        finally
        {
            interrupted.Dispose();
            SteamCertificateAuthority.RecoverTrust(path);
        }
    }

    private static string[] RecordedKeys(string path)
    {
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return record.RootElement.GetProperty("Keys").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "YueYu-CA-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void LocalCertificatesAreLimitedToSelectedDomainsAndValidateAsServerCertificates()
    {
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false });
        var leaf = certificates.CertificateFor("steamcommunity.com");
        Assert.True(leaf.MatchesHostname("steamcommunity.com"));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificates.RootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(leaf));
        Assert.Throws<System.Security.Authentication.AuthenticationException>(() => certificates.CertificateFor("example.com"));
        Assert.Throws<System.Security.Authentication.AuthenticationException>(() => certificates.CertificateFor("store.steampowered.com"));
    }

    [Fact]
    public async Task RealTlsClientCanUseBridgeWithoutExplicitProxyAndWithoutSystemTrustChanges()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false });
        var upstream = new TcpListener(IPAddress.Loopback, 0); upstream.Start();
        var upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        var reply = Task.Run(async () =>
        {
            using var peer = await upstream.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream(); var header = new StringBuilder(); var one = new byte[1];
            while (header.Length < 8192 && !header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { if (await stream.ReadAsync(one, timeout.Token) == 0) break; header.Append((char)one[0]); }
            Assert.Contains("Host: steamcommunity.com", header.ToString());
            await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nprobe"u8.ToArray(), timeout.Token);
        }, timeout.Token);
        using var bridge = new SteamTlsBridge(certificates, async (_, token) =>
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, upstreamPort, token);
            return new NetworkStream(socket, true);
        });
        try
        {
            bridge.Start(0);
            using var handler = new SocketsHttpHandler { UseProxy = false };
            handler.SslOptions.CertificateChainPolicy = new() { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(certificates.RootCertificate);
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, bridge.Port, token);
                return new NetworkStream(socket, true);
            };
            using var http = new HttpClient(handler);
            Assert.Equal("probe", await http.GetStringAsync(new Uri("https://steamcommunity.com/"), timeout.Token));
            await reply;
            while (bridge.TransferredConnections == 0) await Task.Delay(10, timeout.Token);
            Assert.Equal(1, bridge.AcceptedConnections);
            Assert.Equal(1, bridge.UpstreamTlsSuccesses);
            Assert.Equal(1, bridge.TransferredConnections);
            Assert.Contains("上游验证成功 1", bridge.Counters);
            Assert.Contains("实际传输 1", bridge.Counters);
            Assert.Contains("双向字节", bridge.Counters);
        }
        catch (Exception error) { throw new IOException("TLS bridge diagnostic: " + bridge.Counters, error); }
        finally { upstream.Stop(); }
    }

    [Fact]
    public async Task TransferMetricRequiresBothDirectionsAndUpdatesWhileConnectionIsOpen()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false });
        var oneWayUpstream = new ControlledUpstreamStream();
        var twoWayUpstream = new ControlledUpstreamStream();
        var upstreams = new Queue<Stream>([oneWayUpstream, twoWayUpstream]);
        using var bridge = new SteamTlsBridge(certificates, (_, _) => Task.FromResult(upstreams.Dequeue()));
        bridge.Start(0);

        var (oneWayClient, oneWayTls) = await ConnectTlsClientAsync(bridge.Port, certificates, timeout.Token);
        await oneWayTls.WriteAsync("request-one-way"u8.ToArray(), timeout.Token);
        await oneWayUpstream.RequestWritten.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, bridge.TransferredConnections);
        await oneWayTls.DisposeAsync();
        oneWayClient.Dispose();
        await oneWayUpstream.Disposed.Task.WaitAsync(timeout.Token);
        await WaitUntilAsync(() => bridge.ActiveUpstreams == 0, timeout.Token);
        Assert.Equal(0, bridge.TransferredConnections);

        var (twoWayClient, twoWayTls) = await ConnectTlsClientAsync(bridge.Port, certificates, timeout.Token);
        await twoWayTls.WriteAsync("request-two-way"u8.ToArray(), timeout.Token);
        await twoWayUpstream.RequestWritten.Task.WaitAsync(timeout.Token);
        Assert.Equal(0, bridge.TransferredConnections);

        await twoWayUpstream.PushResponseAsync("reply"u8.ToArray());
        var response = new byte[5];
        await twoWayTls.ReadExactlyAsync(response, timeout.Token);

        Assert.Equal("reply", Encoding.ASCII.GetString(response));
        await WaitUntilAsync(
            () => bridge.TransferredConnections == 1,
            TimeSpan.FromSeconds(1),
            timeout.Token);
        Assert.Equal(1, bridge.TransferredConnections);
        Assert.Equal(1, bridge.ActiveUpstreams);

        await twoWayTls.DisposeAsync();
        twoWayClient.Dispose();
    }

    [Fact]
    public async Task WarmUpOpensOneVerifiedConnectionPerHost()
    {
        var hosts = new List<string>();
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false });
        using var bridge = new SteamTlsBridge(certificates, (host, _) =>
        {
            hosts.Add(host);
            return Task.FromResult<Stream>(new MemoryStream());
        });

        var summary = await bridge.WarmUpAsync(["steamcommunity.com", "store.steampowered.com"], CancellationToken.None);

        Assert.Equal(["steamcommunity.com", "store.steampowered.com"], hosts);
        Assert.Contains("预热完成", summary);
    }

    [Fact]
    public async Task WarmUpReportsAControlledFailureInsteadOfThrowing()
    {
        using var certificates = new SteamCertificateAuthority(new() { Store = false, Content = false });
        using var bridge = new SteamTlsBridge(certificates, (_, _) =>
            Task.FromException<Stream>(new SteamUpstreamConnectException("SniBlocked")));

        var summary = await bridge.WarmUpAsync(["steamcommunity.com"], CancellationToken.None);

        Assert.Contains("预热未完成", summary);
        Assert.Contains("SniBlocked", summary);
    }

    private static async Task<(TcpClient Client, SslStream Tls)> ConnectTlsClientAsync(int port, SteamCertificateAuthority certificates, CancellationToken token)    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        var tls = new SslStream(client.GetStream());
        var chain = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        chain.CustomTrustStore.Add(certificates.RootCertificate);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "steamcommunity.com",
            CertificateChainPolicy = chain,
            ApplicationProtocols = [SslApplicationProtocol.Http11]
        }, token);
        return (client, tls);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan maximumWait, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(maximumWait);
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class ControlledUpstreamStream : Stream
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        public TaskCompletionSource<byte[]> RequestWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public async Task PushResponseAsync(byte[] response) => await _incoming.Writer.WriteAsync(response);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                try { _current = await _incoming.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
                _offset = 0;
            }
            var size = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, size).CopyTo(buffer);
            _offset += size;
            return size;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequestWritten.TrySetResult(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => RequestWritten.TrySetResult(buffer.AsSpan(offset, count).ToArray());
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _incoming.Writer.TryComplete();
                Disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }
    }
}
