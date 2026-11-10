using System.Security.Cryptography;
using System.Text.Json;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Windows.Assistant;

public sealed class AssistantCredentialStore(string path) : IAssistantCredentialStore, IDisposable
{
    private readonly string _path = Path.GetFullPath(path);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly byte[] Entropy = "YueYuOrb.Assistant.Credentials.v1"u8.ToArray();
    public void Dispose() => _gate.Dispose();

    private static string Key(string purpose, string endpoint)
    {
        var profilePurpose = purpose.StartsWith("profile:", StringComparison.Ordinal) && Guid.TryParseExact(purpose[8..], "N", out var id) && id != Guid.Empty;
        if (purpose is not ("chat" or "asr") && !profilePurpose || !AssistantEndpointPolicy.IsValid(endpoint)) throw new ArgumentException("凭据用途或接口无效。");
        return purpose + ":" + new Uri(endpoint).AbsoluteUri.TrimEnd('/');
    }

    public async Task<string?> GetAsync(string purpose, string endpoint, CancellationToken token)
    {
        var key = Key(purpose, endpoint); await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return (await ReadAsync(token).ConfigureAwait(false)).GetValueOrDefault(key); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string purpose, string endpoint, string secret, CancellationToken token)
    {
        var key = Key(purpose, endpoint);
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 8192 || secret.Any(char.IsControl)) throw new ArgumentException("API密钥为空或包含无效字符。", nameof(secret));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { var values = await ReadAsync(token).ConfigureAwait(false); values[key] = secret.Trim(); await WriteAsync(values, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string purpose, string endpoint, CancellationToken token)
    {
        var key = Key(purpose, endpoint); await _gate.WaitAsync(token).ConfigureAwait(false);
        try { var values = await ReadAsync(token).ConfigureAwait(false); if (values.Remove(key)) await WriteAsync(values, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return [];
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException("助手凭据库超过限制。");
        var encrypted = await File.ReadAllBytesAsync(_path, token).ConfigureAwait(false);
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new InvalidDataException("助手凭据无法解密，请在原Windows账户中重新保存。旧文件未更改。"); }
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext) ?? throw new InvalidDataException("助手凭据格式无效。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task WriteAsync(Dictionary<string, string> values, CancellationToken token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(values); byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                await stream.WriteAsync(encrypted, token).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
