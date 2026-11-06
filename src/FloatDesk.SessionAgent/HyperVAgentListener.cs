using FloatDesk.Windows.DesktopSessions;
using System.Security.Cryptography;

namespace FloatDesk.SessionAgent;

public sealed record HyperVAgentListenerOptions(
    Guid VmId,
    Guid ServiceId,
    SessionAgentBinding Binding,
    byte[] PairingSecret);

public static class HyperVAgentListener
{
    public static async Task ServeAsync(
        Stream stream,
        HyperVAgentListenerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PairingSecret.Length !=
            HyperVPairingProtocol.SecretSize)
        {
            throw new ArgumentException(
                "The Hyper-V pairing secret must be 32 bytes.",
                nameof(options));
        }

        var pairingSecret = options.PairingSecret.ToArray();
        using var timeout = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SessionProtocol.HandshakeTimeout);
        var hello =
            await SessionProtocolJson.ReadAsync<HyperVPairingClientHello>(
                stream,
                timeout.Token).ConfigureAwait(false);
        if (hello is null ||
            hello.Version != HyperVPairingProtocol.CurrentVersion ||
            hello.VmId != options.VmId ||
            hello.ServiceId != options.ServiceId)
        {
            await SessionProtocolJson.WriteAsync(
                stream,
                new HyperVPairingServerHello(
                    HyperVPairingProtocol.CurrentVersion,
                    false,
                    options.VmId,
                    options.ServiceId,
                    [],
                    [],
                    "IdentityRejected"),
                timeout.Token).ConfigureAwait(false);
            return;
        }

        var serverHello = HyperVPairingProtocol.CreateServerHello(
            pairingSecret,
            hello);
        await SessionProtocolJson.WriteAsync(
            stream,
            serverHello,
            timeout.Token).ConfigureAwait(false);
        var clientProof =
            await SessionProtocolJson.ReadAsync<HyperVPairingClientProof>(
                stream,
                timeout.Token).ConfigureAwait(false);
        var claimedBinding = clientProof?.Binding;
        var binding = new HyperVPairingBinding(
            options.Binding.EnvironmentId,
            options.Binding.Generation,
            options.Binding.LoginInstanceId,
            options.Binding.WindowsSessionId,
            options.Binding.UserSid,
            claimedBinding?.BootId ?? string.Empty,
            claimedBinding?.LoginTimeUtc ?? default);
        var accepted = clientProof is
                { Binding: not null, Proof: not null } proof &&
            proof.Binding.EnvironmentId ==
                options.Binding.EnvironmentId &&
            proof.Binding.Generation ==
                options.Binding.Generation &&
            proof.Binding.LoginInstanceId ==
                options.Binding.LoginInstanceId &&
            proof.Binding.WindowsSessionId ==
                options.Binding.WindowsSessionId &&
            string.Equals(
                proof.Binding.UserSid,
                options.Binding.UserSid,
                StringComparison.OrdinalIgnoreCase) &&
            HyperVPairingProtocol.VerifyClientProof(
                pairingSecret,
                hello,
                serverHello,
                binding,
                proof.Proof);
        await SessionProtocolJson.WriteAsync(
            stream,
            new HyperVPairingAccepted(
                accepted,
                accepted ? null : "AuthenticationRejected"),
            timeout.Token).ConfigureAwait(false);
        if (!accepted)
            return;

        CryptographicOperations.ZeroMemory(pairingSecret);
        await SessionAgentServer.RunProtocolLoopAsync(
            stream,
            options.Binding).ConfigureAwait(false);
    }
}
