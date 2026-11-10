using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.Windows.DesktopSessions;

public interface IDesktopSessionDataProtector
{
    byte[] Protect(byte[] data);

    byte[] Unprotect(byte[] data);
}

public sealed class DpapiDesktopSessionDataProtector :
    IDesktopSessionDataProtector
{
    public byte[] Protect(byte[] data) =>
        ProtectedData.Protect(
            data,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(
            data,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
}

public sealed record HyperVPairingClientHello(
    int Version,
    Guid VmId,
    Guid ServiceId,
    byte[] ClientNonce)
{
    public bool DiscoverOnly { get; init; }
}

public sealed record HyperVPairingServerHello(
    int Version,
    bool Accepted,
    Guid VmId,
    Guid ServiceId,
    byte[] ServerNonce,
    byte[] ServerProof,
    string? ErrorCode);

public sealed record HyperVPairingBinding(
    Guid EnvironmentId,
    Guid Generation,
    Guid LoginInstanceId,
    uint WindowsSessionId,
    string UserSid,
    string BootId,
    DateTimeOffset LoginTimeUtc);

public sealed record HyperVPairingClientProof(
    byte[] Proof,
    HyperVPairingBinding Binding);

public sealed record HyperVPairingAccepted(
    bool Accepted,
    string? ErrorCode);

public static class HyperVPairingProtocol
{
    public const int CurrentVersion = 1;
    public const int NonceSize = 16;
    public const int SecretSize = 32;

    public static HyperVPairingClientHello CreateClientHello(
        Guid vmId,
        Guid serviceId)
    {
        if (vmId == Guid.Empty || serviceId == Guid.Empty)
        {
            throw new ArgumentException(
                "Hyper-V pairing requires a VM and service identity.");
        }

        return new HyperVPairingClientHello(
            CurrentVersion,
            vmId,
            serviceId,
            RandomNumberGenerator.GetBytes(NonceSize));
    }

    public static HyperVPairingServerHello CreateServerHello(
        ReadOnlySpan<byte> secret,
        HyperVPairingClientHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ValidateSecret(secret);
        if (!IsValidHello(hello))
        {
            return new HyperVPairingServerHello(
                CurrentVersion,
                false,
                hello.VmId,
                hello.ServiceId,
                [],
                [],
                "InvalidHello");
        }

        var serverNonce = RandomNumberGenerator.GetBytes(NonceSize);
        return new HyperVPairingServerHello(
            CurrentVersion,
            true,
            hello.VmId,
            hello.ServiceId,
            serverNonce,
            CreateProof(
                secret,
                hello,
                serverNonce,
                serverToClient: true),
            null);
    }

    public static byte[] CreateClientProof(
        ReadOnlySpan<byte> secret,
        HyperVPairingClientHello hello,
        HyperVPairingServerHello serverHello,
        HyperVPairingBinding binding) =>
        CreateProof(
            secret,
            hello,
            serverHello.ServerNonce,
            serverToClient: false,
            binding);

    public static bool VerifyClientProof(
        ReadOnlySpan<byte> secret,
        HyperVPairingClientHello hello,
        HyperVPairingServerHello serverHello,
        HyperVPairingBinding binding,
        ReadOnlySpan<byte> actualProof)
    {
        try
        {
            var expected = CreateClientProof(
                secret,
                hello,
                serverHello,
                binding);
            return expected.Length == actualProof.Length &&
                CryptographicOperations.FixedTimeEquals(
                    expected,
                    actualProof);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static byte[] CreateProof(
        ReadOnlySpan<byte> secret,
        HyperVPairingClientHello hello,
        ReadOnlySpan<byte> serverNonce,
        bool serverToClient,
        HyperVPairingBinding? binding = null)
    {
        ValidateSecret(secret);
        ArgumentNullException.ThrowIfNull(hello);
        if (!IsValidHello(hello) ||
            hello.ClientNonce is null ||
            serverNonce.Length != NonceSize)
        {
            throw new ArgumentException(
                "The Hyper-V pairing handshake is invalid.");
        }

        var role = Encoding.ASCII.GetBytes(
            serverToClient ? "server\0" : "client\0");
        var userSid = Encoding.UTF8.GetBytes(binding?.UserSid ?? string.Empty);
        var bootId = Encoding.UTF8.GetBytes(binding?.BootId ?? string.Empty);
        var bindingSize = binding is null
            ? 0
            : 16 + 16 + 16 + 4 + sizeof(long) + userSid.Length + bootId.Length;
        var input = new byte[
            role.Length +
            NonceSize * 2 +
            16 +
            16 +
            bindingSize];
        var offset = 0;
        role.CopyTo(input, offset);
        offset += role.Length;
        hello.ClientNonce.CopyTo(input, offset);
        offset += NonceSize;
        serverNonce.CopyTo(input.AsSpan(offset));
        offset += NonceSize;
        hello.VmId.TryWriteBytes(input.AsSpan(offset));
        offset += 16;
        hello.ServiceId.TryWriteBytes(input.AsSpan(offset));
        offset += 16;

        if (binding is not null)
        {
            binding.EnvironmentId.TryWriteBytes(input.AsSpan(offset));
            offset += 16;
            binding.Generation.TryWriteBytes(input.AsSpan(offset));
            offset += 16;
            binding.LoginInstanceId.TryWriteBytes(input.AsSpan(offset));
            offset += 16;
            BinaryPrimitives.WriteUInt32LittleEndian(
                input.AsSpan(offset),
                binding.WindowsSessionId);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(
                input.AsSpan(offset),
                binding.LoginTimeUtc.ToUniversalTime().UtcTicks);
            offset += sizeof(long);
            userSid.CopyTo(input, offset);
            offset += userSid.Length;
            bootId.CopyTo(input, offset);
        }

        using var hmac = new HMACSHA256(secret.ToArray());
        return hmac.ComputeHash(input);
    }

    private static bool IsValidHello(HyperVPairingClientHello hello) =>
        hello.Version == CurrentVersion &&
        hello.VmId != Guid.Empty &&
        hello.ServiceId != Guid.Empty &&
        hello.ClientNonce is { Length: NonceSize };

    private static void ValidateSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretSize)
        {
            throw new ArgumentException(
                "The Hyper-V pairing secret must be 32 bytes.",
                nameof(secret));
        }
    }
}

public sealed class HyperVPairingHandshake
{
    private const int MaxRememberedHandshakes = 64;
    private readonly object _sync = new();
    private readonly HashSet<string> _accepted = [];
    private readonly Queue<string> _order = [];

    public bool VerifyServerHello(
        ReadOnlySpan<byte> secret,
        HyperVPairingClientHello hello,
        HyperVPairingServerHello serverHello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        ArgumentNullException.ThrowIfNull(serverHello);
        if (!serverHello.Accepted ||
            serverHello.Version != HyperVPairingProtocol.CurrentVersion ||
            serverHello.VmId != hello.VmId ||
            serverHello.ServiceId != hello.ServiceId ||
            serverHello.ServerNonce is not
                { Length: HyperVPairingProtocol.NonceSize } ||
            serverHello.ServerProof is not { Length: 32 })
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = HyperVPairingProtocol.CreateProof(
                secret,
                hello,
                serverHello.ServerNonce,
                serverToClient: true);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                expected,
                serverHello.ServerProof))
        {
            return false;
        }

        var replayKey = Convert.ToHexString(hello.ClientNonce) +
            Convert.ToHexString(serverHello.ServerNonce);
        lock (_sync)
        {
            if (!_accepted.Add(replayKey))
                return false;
            _order.Enqueue(replayKey);
            while (_order.Count > MaxRememberedHandshakes)
                _accepted.Remove(_order.Dequeue());
        }

        return true;
    }
}
