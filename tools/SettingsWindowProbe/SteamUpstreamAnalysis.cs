using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using FloatDesk.Windows.Network;

namespace SettingsWindowProbe;

internal static class SteamUpstreamAnalysis
{
    private static readonly bool[] SniModes = [true, false];
    public static async Task RunAsync()
    {
        using var resolver = new SteamNetworkResolver();
        string[] hosts = ["store.steampowered.com", "steamcommunity.com", "api.steampowered.com", "login.steampowered.com", "community.cloudflare.steamstatic.com", "shared.fastly.steamstatic.com"];
        await Task.WhenAll(hosts.Select(async host =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var addresses = await resolver.ResolveAsync(host, true, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { host, dnsAddresses = addresses.Select(address => address.ToString()).ToArray() }));
            foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork).Take(2))
            foreach (var noSni in SniModes)
                Console.WriteLine(await TestAsync(host, address, noSni));
        }));
    }

    private static async Task<string> TestAsync(string host, IPAddress address, bool noSni)
    {
        var result = new Dictionary<string, object?> { ["host"] = host, ["ip"] = address.ToString(), ["mode"] = noSni ? "no-sni" : "original-sni" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        var watch = Stopwatch.StartNew();
        var stage = "tcp";
        try
        {
            await socket.ConnectAsync(address, 443, timeout.Token);
            using var tls = new SslStream(new NetworkStream(socket, false), false, (_, certificate, _, errors) =>
            {
                result["certificateErrors"] = errors.ToString();
                return SteamUpstreamTls.ValidateCertificate(host, certificate, errors);
            });
            stage = "tls";
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = noSni ? address.ToString() : host, ApplicationProtocols = [SslApplicationProtocol.Http11] }, timeout.Token);
            result["tlsAuthenticated"] = true;
            stage = "http-status";
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + host + "\r\nConnection: close\r\n\r\n"), timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, true);
            var status = await reader.ReadLineAsync(timeout.Token);
            result["status"] = status is not null && status.StartsWith("HTTP/", StringComparison.Ordinal) ? status : "No HTTP status line";
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException)
        {
            result["failedStage"] = stage;
            result["errorType"] = error.GetType().Name;
            result["baseErrorType"] = error.GetBaseException().GetType().Name;
            if (error.GetBaseException() is SocketException socketError) result["socketError"] = socketError.SocketErrorCode.ToString();
        }
        result["elapsedMs"] = watch.ElapsedMilliseconds;
        return JsonSerializer.Serialize(result);
    }
}
