using System.Net;

namespace FloatDesk.Windows.Network;

public enum SteamDnsSource
{
    HttpsDns,
    SystemDns
}

public sealed record SteamNetworkDiagnostic(
    string Host,
    string? Address,
    string Source,
    string SniMode,
    string Stage,
    int ElapsedMilliseconds,
    string ErrorCode,
    bool CertificateValidated)
{
    /// <summary>Controlled resolver identifier (DNSPod / 360 / Cloudflare / Google / system / none).</summary>
    public string? Provider { get; init; }

    /// <summary>Controlled record family (A / AAAA) when the entry describes a DNS answer.</summary>
    public string? RecordType { get; init; }

    /// <summary>Bounded, whitelisted detail such as "DNSPod:Timeout,360:NoAnswer". Never raw exception text.</summary>
    public string? Detail { get; init; }

    internal static SteamNetworkDiagnostic Create(
        string host,
        SteamResolvedAddress? candidate,
        string sniMode,
        string stage,
        long elapsedMilliseconds,
        string errorCode,
        bool certificateValidated) => new(
            host,
            candidate?.Address.ToString(),
            candidate?.Source switch
            {
                SteamDnsSource.HttpsDns => "https-dns",
                SteamDnsSource.SystemDns => "system-dns",
                _ => "none"
            },
            sniMode,
            stage,
            (int)Math.Clamp(elapsedMilliseconds, 0, int.MaxValue),
            errorCode,
            certificateValidated);
}

public sealed record SteamNetworkFailure(DateTimeOffset AtUtc, SteamNetworkDiagnostic Diagnostic);

internal sealed record SteamResolvedAddress(IPAddress Address, SteamDnsSource Source);
