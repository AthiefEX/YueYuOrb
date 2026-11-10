using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

/// <summary>Where a probe actually travelled. The route decides what a result is allowed to prove.</summary>
public static class SteamProbeRoute
{
    public const string SystemDirect = "SystemDirect";
    public const string IndependentDnsDirect = "IndependentDnsDirect";
    public const string LocalBridge = "LocalBridge";
}

/// <summary>Structured, log-safe probe result. A single probe never proves the whole feature.</summary>
public sealed record SteamNetworkProbeResult(
    string ProbeId,
    string Route,
    string Host,
    string Stage,
    int? HttpStatus,
    bool ReachedLocalBridge,
    string ErrorCode,
    long DurationMs)
{
    public bool Usable => ErrorCode == "None" && HttpStatus is >= 200 and < 400;
}

public sealed record SteamBridgeCounters(long Accepted, long Transferred, long BytesToUpstream, long BytesToDownstream);

public static class SteamCompatibilityDiagnostics
{
    public static async Task<string> RunAsync(SteamNetworkSettings settings, bool useHosts, CancellationToken token)
    {
        var results = await RunDetailedAsync(settings, useHosts ? SteamProbeRoute.LocalBridge : SteamProbeRoute.IndependentDnsDirect, null, token).ConfigureAwait(false);
        return Format(results);
    }

    public static string Format(IReadOnlyList<SteamNetworkProbeResult> results)
    {
        if (results.Count == 0) return "请先选择商店或社区站点。";
        return string.Join(Environment.NewLine, results.Select(result =>
        {
            var route = result.Route switch
            {
                SteamProbeRoute.LocalBridge => "经月语",
                SteamProbeRoute.IndependentDnsDirect => "独立解析直连",
                _ => "系统直连"
            };
            var detail = result.HttpStatus is { } status ? "HTTP " + status : result.ErrorCode;
            var verdict = result.Usable ? "（入口响应正常）" : "（未通过：" + result.ErrorCode + "）";
            return $"{result.Host}：{route} {detail}{verdict}" + (result.Route == SteamProbeRoute.LocalBridge && !result.ReachedLocalBridge ? "；未命中本机桥接" : "");
        }));
    }

    public static IReadOnlyList<string> Targets(SteamNetworkSettings settings)
    {
        var urls = new List<string>();
        if (settings.Community)
        {
            urls.Add("https://steamcommunity.com/");
            urls.Add("https://steamcommunity.com/workshop/");
        }
        if (settings.Store) urls.Add("https://store.steampowered.com/");
        return urls;
    }

    public static async Task<IReadOnlyList<SteamNetworkProbeResult>> RunDetailedAsync(
        SteamNetworkSettings settings,
        string route,
        Func<SteamBridgeCounters>? snapshot,
        CancellationToken token)
    {
        var before = snapshot?.Invoke();
        var targets = Targets(settings);
        if (targets.Count == 0) return [];

        using var resolver = route == SteamProbeRoute.IndependentDnsDirect ? new SteamNetworkResolver() : null;
        var results = await Task.WhenAll(targets.Select((url, index) => ProbeAsync(settings, route, resolver, url, index, before, snapshot, token))).ConfigureAwait(false);
        return results;
    }

    private static async Task<SteamNetworkProbeResult> ProbeAsync(
        SteamNetworkSettings settings,
        string route,
        SteamNetworkResolver? resolver,
        string url,
        int index,
        SteamBridgeCounters? before,
        Func<SteamBridgeCounters>? snapshot,
        CancellationToken token)
    {
        var target = new Uri(url);
        var host = target.Host;
        var timer = Stopwatch.StartNew();
        var stage = "connect";
        var reachedBridge = false;
        using var handler = CreateHandler(route, resolver, host);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            stage = "http";
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                var locationHost = location is null ? null : location.IsAbsoluteUri ? location.Host : host;
                if (locationHost is not null && !SteamNetworkPolicy.IsAllowed(locationHost, settings))
                {
                    return Result("RedirectBlocked", "redirect", status, elapsed: timer);
                }
            }

            if (route == SteamProbeRoute.LocalBridge)
                reachedBridge = BridgeAdvanced(before, snapshot);
            return Result(status < 400 ? "None" : "Http" + status, stage, status, reachedBridge, timer);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            token.ThrowIfCancellationRequested();
            var code = error is OperationCanceledException ? "Timeout" : Classify(error);
            if (route == SteamProbeRoute.LocalBridge)
                reachedBridge = BridgeAdvanced(before, snapshot);
            return Result(code, stage, null, reachedBridge, timer);
        }

        SteamNetworkProbeResult Result(string code, string atStage, int? status, bool bridgeHit = false, Stopwatch? elapsed = null) =>
            new("probe-" + index, route, host, atStage, status, bridgeHit, code, (elapsed ?? timer).ElapsedMilliseconds);
    }

    private static bool BridgeAdvanced(SteamBridgeCounters? before, Func<SteamBridgeCounters>? snapshot)
    {
        if (before is null || snapshot is null) return false;
        var after = snapshot();
        return after.Transferred > before.Transferred &&
            after.Accepted > before.Accepted &&
            after.BytesToDownstream > before.BytesToDownstream;
    }

    private static SocketsHttpHandler CreateHandler(string route, SteamNetworkResolver? resolver, string host)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        switch (route)
        {
            case SteamProbeRoute.IndependentDnsDirect when resolver is not null:
                handler.ConnectCallback = async (context, cancellation) =>
                {
                    var addresses = await resolver.ResolveAsync(context.DnsEndPoint.Host, true, cancellation).ConfigureAwait(false);
                    foreach (var address in addresses.Take(6))
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        attempt.CancelAfter(TimeSpan.FromSeconds(4));
                        try { await socket.ConnectAsync(address, 443, attempt.Token).ConfigureAwait(false); return new NetworkStream(socket, true); }
                        catch (Exception error) when (error is SocketException or OperationCanceledException) { socket.Dispose(); cancellation.ThrowIfCancellationRequested(); }
                    }
                    throw new IOException("没有可用公网端点。");
                };
                break;
            case SteamProbeRoute.LocalBridge:
                handler.ConnectCallback = async (_, cancellation) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(IPAddress.Loopback, 443, cancellation).ConfigureAwait(false);
                        return new NetworkStream(socket, true);
                    }
                    catch { socket.Dispose(); throw; }
                };
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null) return false;
                    try
                    {
                        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                        // Only the YueYu local CA may issue this leaf; the probe pins that identity
                        // instead of disabling verification for arbitrary certificates.
                        if (leaf.Issuer.StartsWith("CN=YueYu Steam Background CA ", StringComparison.Ordinal))
                            return errors == SslPolicyErrors.None && leaf.MatchesHostname(host);
                        return leaf.Issuer.Contains("YueYu Steam Local CA", StringComparison.Ordinal) && leaf.MatchesHostname(host);
                    }
                    catch (System.Security.Cryptography.CryptographicException) { return false; }
                };
                break;
        }
        return handler;
    }

    private static string Classify(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException) return "TlsFailed";
            if (current is SocketException socket)
                return socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted ? "ConnectionReset" : "ConnectFailed";
        }
        return "ConnectFailed";
    }
}
