using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Windows.Translation;

public sealed class DpapiTranslationCredentialStore : ITranslationCredentialStore, IDisposable
{
    private const int CurrentVersion = 1;
    private readonly string _filePath;
    private readonly ITranslationDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiTranslationCredentialStore(string filePath, ITranslationDataProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _protector = protector ?? new DpapiTranslationDataProtector();
    }

    public void Dispose() => _gate.Dispose();

    public async Task<string?> GetAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = await ReadAsync(cancellationToken);
            return credentials.GetValueOrDefault(TranslationCredentialKey.For(provider, endpoint));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = await ReadAsync(cancellationToken);
            return credentials.ContainsKey(TranslationCredentialKey.For(provider, endpoint));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TranslationProviderKind provider, string endpoint, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = await ReadAsync(cancellationToken);
            credentials[TranslationCredentialKey.For(provider, endpoint)] = apiKey;
            await WriteAsync(credentials, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var credentials = await ReadAsync(cancellationToken);
            if (credentials.Remove(TranslationCredentialKey.For(provider, endpoint)))
                await WriteAsync(credentials, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            var ciphertext = await File.ReadAllBytesAsync(_filePath, cancellationToken);
            byte[] plaintext;
            try
            {
                plaintext = _protector.Unprotect(ciphertext);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return [];
            }

            var document = JsonSerializer.Deserialize<CredentialDocument>(plaintext);
            return document is { Version: CurrentVersion, Credentials: not null }
                ? document.Credentials
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    private async Task WriteAsync(Dictionary<string, string> credentials, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new CredentialDocument(CurrentVersion, credentials));
        byte[] ciphertext;
        try
        {
            ciphertext = _protector.Protect(plaintext);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("Unable to protect translation credentials.");
        }

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, ciphertext, cancellationToken);
            if (File.Exists(_filePath))
                File.Replace(temporaryPath, _filePath, null);
            else
                File.Move(temporaryPath, _filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record CredentialDocument(int Version, Dictionary<string, string> Credentials);
}
