using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace SettingsWindowProbe;

/// <summary>
/// Read-only end-to-end probe of the local TLS bridge: it does not touch hosts, the system proxy
/// or the machine certificate stores, so it can run while the user instance keeps running.
/// </summary>
internal static class SteamBridgeProbe
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    public static async Task RunAsync(string host, bool useNoSni = true, string? dataDirectory = null, int repeat = 1)
    {
        var settings = new SteamNetworkSettings { UseNoSniCompatibility = useNoSni };
        using var certificates = new SteamCertificateAuthority(settings);
        var events = new System.Collections.Concurrent.ConcurrentQueue<SteamNetworkDiagnostic>();
        using var bridge = new SteamTlsBridge(certificates, settings, diagnostic => events.Enqueue(diagnostic), dataDirectory);
        bridge.Start(0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = new Dictionary<string, object?> { ["host"] = host, ["route"] = "local-bridge", ["port"] = bridge.Port, ["noSniPreference"] = useNoSni };
        var requests = new List<Dictionary<string, object?>>();
        for (var run = 0; run < Math.Max(1, repeat); run++)
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.Zero };
            handler.ConnectCallback = async (_, cancellation) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(IPAddress.Loopback, bridge.Port, cancellation);
                return new NetworkStream(socket, true);
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                return leaf.Issuer.Contains("YueYu Steam Local CA", StringComparison.Ordinal) && leaf.MatchesHostname(new Uri("https://" + host).Host);
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            var single = new Dictionary<string, object?>();
            var watch = Stopwatch.StartNew();
            try
            {
                using var response = await http.GetAsync(new Uri("https://" + host), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                single["status"] = (int)response.StatusCode;
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (total < 512 * 1024)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(3));
                    int size;
                    try { size = await body.ReadAsync(buffer, idle.Token); }
                    catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { break; }
                    if (size == 0) break;
                    total += size;
                }
                single["bodyBytes"] = total;
                single["contentType"] = response.Content.Headers.ContentType?.MediaType;
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
            {
                single["error"] = error.GetType().Name + " / " + error.GetBaseException().GetType().Name;
            }
            single["elapsedMs"] = watch.ElapsedMilliseconds;
            requests.Add(single);
            result["status"] = single.GetValueOrDefault("status");
            result["error"] = single.GetValueOrDefault("error");
            result["bodyBytes"] = single.GetValueOrDefault("bodyBytes");
            result["elapsedMs"] = single.ElapsedMillisecondsOrZero();
        }
        result["requests"] = requests;
        result["counters"] = bridge.Counters;
        result["events"] = events.Select(item => new { item.Host, item.Stage, item.ErrorCode, item.Provider, item.RecordType, item.SniMode, item.Address, item.ElapsedMilliseconds, item.Detail }).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(result, Format));
    }

    private static long ElapsedMillisecondsOrZero(this Dictionary<string, object?> entry) =>
        entry.TryGetValue("elapsedMs", out var value) && value is long milliseconds ? milliseconds : 0;
}
