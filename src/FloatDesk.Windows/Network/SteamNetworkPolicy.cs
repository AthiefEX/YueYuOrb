using System.Net;
using System.Net.Sockets;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

public static class SteamNetworkPolicy
{
    private static readonly string[] StoreDomains = ["store.steampowered.com", "checkout.steampowered.com", "help.steampowered.com", "login.steampowered.com", "api.steampowered.com"];
    private static readonly string[] ContentDomains = ["community.steamstatic.com", "community.akamai.steamstatic.com", "community.fastly.steamstatic.com", "community.cloudflare.steamstatic.com", "store.akamai.steamstatic.com", "store.fastly.steamstatic.com", "store.cloudflare.steamstatic.com", "shared.akamai.steamstatic.com", "shared.fastly.steamstatic.com", "shared.cloudflare.steamstatic.com", "avatars.steamstatic.com", "avatars.akamai.steamstatic.com", "avatars.fastly.steamstatic.com", "steamuserimages-a.akamaihd.net", "steamcommunity-a.akamaihd.net"];
    public static string[] Domains(SteamNetworkSettings settings) =>
        (settings.Store ? StoreDomains : [])
        .Concat(settings.Community ? ["steamcommunity.com", "www.steamcommunity.com"] : [])
        .Concat(settings.Content ? ContentDomains : []).ToArray();

    public static bool IsAllowed(string host, SteamNetworkSettings settings) => Domains(settings).Contains(host, StringComparer.OrdinalIgnoreCase);

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if ((b[0] & 0xe0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] <= 0x01) return false; // 2001::/23 special-purpose
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x02) return false; // 2002::/16 6to4
            if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0) return false; // 3fff::/20 documentation
            return true;
        }

        if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
        if (b[0] == 100 && b[1] is >= 64 and <= 127) return false; // 100.64.0.0/10
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        if (b[0] == 192 && (b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99 || b[1] == 168)) return false;
        if (b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)) return false;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
        return true;
    }

    public static string BuildPac(SteamNetworkSettings settings, int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var match = string.Join(" || ", Domains(settings).Select(host => $"host === '{host}'"));
        return "function FindProxyForURL(url, host) { host = host.toLowerCase(); " +
            $"if (url.substring(0, 6).toLowerCase() === 'https:' && ({(match.Length == 0 ? "false" : match)})) return 'PROXY 127.0.0.1:{port}; DIRECT'; return 'DIRECT'; }}";
    }
}
