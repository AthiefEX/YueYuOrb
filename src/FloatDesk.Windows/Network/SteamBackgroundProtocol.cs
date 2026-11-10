namespace FloatDesk.Windows.Network;

public sealed record SteamBackgroundRequest(string Command, int Mask = 0, string? Host = null, string? Csr = null, string? Thumbprint = null);
public sealed record SteamBackgroundReply(bool Success, string Code, string? Certificate = null, string? Thumbprint = null);

public static class SteamBackgroundProtocol
{
    public const string ServiceName = "YueYuOrbSteamNetwork";
    public const string PipeName = "YueYuOrb.SteamNetwork.v1";
    public const int MaximumMessageLength = 16384;
    public static bool IsAllowed(string command, int mask, string? host) => command switch
    {
        "STATUS" => mask == 0 && host is null,
        "LEASE" => mask is >= 0 and <= 7 && host is null,
        "SIGN" => mask == 0 && host is not null && SteamNetworkPolicy.IsAllowed(host, new()),
        _ => false
    };
}
