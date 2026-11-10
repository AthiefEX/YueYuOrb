namespace FloatDesk.Core.Models;

public sealed record SteamNetworkSettings
{
    public bool UseHostsCompatibility { get; init; } = true;
    public bool Store { get; init; } = true;
    public bool Community { get; init; } = true;
    public bool Content { get; init; } = true;
    public bool UseHttpsDns { get; init; } = true;

    /// <summary>
    /// Allows a second upstream attempt without a TLS SNI extension when the first handshake is
    /// reset before any certificate is offered. The upstream certificate chain and the original
    /// host name are still fully validated, so this does not weaken verification.
    /// Defaults to enabled because the shipping networks reset ClientHello for these host names.
    /// </summary>
    public bool UseNoSniCompatibility { get; init; } = true;
    public bool UseSystemPac { get; init; }
    public bool ShowInWheel { get; init; }
}
