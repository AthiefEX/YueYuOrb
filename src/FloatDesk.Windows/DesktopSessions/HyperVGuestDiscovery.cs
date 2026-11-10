using System.Security.Cryptography;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record HyperVGuestDiscoveryResponse(HyperVGuestIdentity Identity, byte[] Proof);

public static class HyperVGuestDiscovery
{
    public static Guid ServiceId { get; } = new("901aa695-6edb-4ec7-b30b-e8d29b10c321");

    public static byte[] CreateProof(byte[] secret, byte[] nonce, HyperVGuestIdentity identity)
    {
        if (secret.Length != 32 || nonce.Length != 16)
            throw new ArgumentException("Invalid discovery key or nonce.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { Purpose = "YueYu-Guest-Discovery-v1", Nonce = nonce, Identity = identity });
        return HMACSHA256.HashData(secret, payload);
    }

    public static bool VerifyProof(byte[] secret, byte[] nonce, HyperVGuestIdentity identity, byte[] proof) =>
        CryptographicOperations.FixedTimeEquals(CreateProof(secret, nonce, identity), proof);
}

public sealed class HyperVGuestAgentProbe(IHyperVPairingSecretStore secrets) : IHyperVGuestAgentProbe
{
    public async Task<HyperVGuestIdentity> ProbeAsync(Guid expectedVmId, CancellationToken cancellationToken)
    {
        var secret = await secrets.LoadAsync(expectedVmId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("HyperVGuestAgentNotPaired");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SessionProtocol.HandshakeTimeout);
            await using var stream = await new WindowsHyperVSocketConnector().ConnectAsync(expectedVmId, HyperVGuestDiscovery.ServiceId, timeout.Token).ConfigureAwait(false);
            var hello = HyperVPairingProtocol.CreateClientHello(expectedVmId, HyperVGuestDiscovery.ServiceId) with { DiscoverOnly = true };
            await SessionProtocolJson.WriteAsync(stream, hello, timeout.Token).ConfigureAwait(false);
            var response = await SessionProtocolJson.ReadAsync<HyperVGuestDiscoveryResponse>(stream, timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("HyperVGuestIdentityMissing");
            if (response.Identity is null || response.Proof is null || response.Identity.VmId != expectedVmId ||
                response.Identity.ServiceId != HyperVGuestDiscovery.ServiceId ||
                !HyperVGuestDiscovery.VerifyProof(secret, hello.ClientNonce, response.Identity, response.Proof))
                throw new InvalidOperationException("HyperVGuestIdentityRejected");
            return response.Identity;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "虚拟机已运行，但来宾 Agent 尚未响应。请在控制台登录 Windows，并确认月语 Agent 正在运行；自动任务暂不可用。", exception);
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
