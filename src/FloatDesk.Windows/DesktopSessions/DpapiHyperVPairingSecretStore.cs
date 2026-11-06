using System.Buffers.Binary;
using System.Text;

namespace FloatDesk.Windows.DesktopSessions;

public interface IHyperVPairingSecretStore
{
    Task SaveAsync(
        Guid vmId,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken);

    Task<byte[]?> LoadAsync(
        Guid vmId,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid vmId,
        CancellationToken cancellationToken);
}

public sealed class DpapiHyperVPairingSecretStore :
    IHyperVPairingSecretStore
{
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes("YYHVP001");
    private readonly string _directory;
    private readonly IDesktopSessionDataProtector _protector;

    public DpapiHyperVPairingSecretStore(
        string? directory = null,
        IDesktopSessionDataProtector? protector = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "FloatDesk",
            "desktop-sessions");
        _protector = protector ??
            new DpapiDesktopSessionDataProtector();
    }

    public async Task SaveAsync(
        Guid vmId,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        ValidateVmId(vmId);
        if (secret.Length != HyperVPairingProtocol.SecretSize)
        {
            throw new ArgumentException(
                "The Hyper-V pairing secret must be 32 bytes.",
                nameof(secret));
        }

        Directory.CreateDirectory(_directory);
        var protectedSecret = _protector.Protect(secret.ToArray());
        var envelope = new byte[
            Magic.Length + 16 + sizeof(int) + protectedSecret.Length];
        Magic.CopyTo(envelope, 0);
        vmId.TryWriteBytes(envelope.AsSpan(Magic.Length));
        BinaryPrimitives.WriteInt32LittleEndian(
            envelope.AsSpan(Magic.Length + 16),
            protectedSecret.Length);
        protectedSecret.CopyTo(
            envelope,
            Magic.Length + 16 + sizeof(int));

        var path = GetPath(vmId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                envelope,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task<byte[]?> LoadAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        ValidateVmId(vmId);
        var path = GetPath(vmId);
        if (!File.Exists(path))
            return null;

        var envelope = await File.ReadAllBytesAsync(
            path,
            cancellationToken).ConfigureAwait(false);
        if (envelope.Length < Magic.Length + 16 + sizeof(int) ||
            !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
            !envelope.AsSpan(Magic.Length, 16).SequenceEqual(
                vmId.ToByteArray()))
        {
            throw new InvalidDataException(
                "The Hyper-V pairing secret envelope is invalid.");
        }

        var protectedLength = BinaryPrimitives.ReadInt32LittleEndian(
            envelope.AsSpan(Magic.Length + 16));
        if (protectedLength <= 0 ||
            protectedLength !=
                envelope.Length - Magic.Length - 16 - sizeof(int))
        {
            throw new InvalidDataException(
                "The Hyper-V pairing secret length is invalid.");
        }

        var secret = _protector.Unprotect(
            envelope.AsSpan(
                Magic.Length + 16 + sizeof(int),
                protectedLength).ToArray());
        if (secret.Length != HyperVPairingProtocol.SecretSize)
        {
            throw new InvalidDataException(
                "The Hyper-V pairing secret has an invalid format.");
        }

        return secret;
    }

    public Task DeleteAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        ValidateVmId(vmId);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(vmId);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string GetPath(Guid vmId) =>
        Path.Combine(_directory, $"{vmId:N}.pairing");

    private static void ValidateVmId(Guid vmId)
    {
        if (vmId == Guid.Empty)
            throw new ArgumentException("The VM ID is required.", nameof(vmId));
    }
}
