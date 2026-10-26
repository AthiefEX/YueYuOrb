using System.Text;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Translation;

namespace FloatDesk.Windows.Tests;

public sealed class DpapiTranslationCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "floatdesk-credentials-" + Guid.NewGuid().ToString("N"));
    private readonly XorTestProtector _protector = new(0x5A);

    public DpapiTranslationCredentialStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CredentialRoundTripsUnderProviderAndEndpoint()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        var store = new DpapiTranslationCredentialStore(path, _protector);

        await store.SaveAsync(TranslationProviderKind.DeepL, "https://api-free.deepl.com", "secret-key", CancellationToken.None);

        Assert.True(await store.HasAsync(TranslationProviderKind.DeepL, "https://api-free.deepl.com", CancellationToken.None));
        Assert.Equal("secret-key", await new DpapiTranslationCredentialStore(path, _protector)
            .GetAsync(TranslationProviderKind.DeepL, "https://api-free.deepl.com", CancellationToken.None));
        Assert.Null(await store.GetAsync(TranslationProviderKind.DeepL, "https://other.example.com", CancellationToken.None));
        Assert.Null(await store.GetAsync(TranslationProviderKind.MyMemory, "https://api-free.deepl.com", CancellationToken.None));
        Assert.DoesNotContain("secret-key", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        Assert.DoesNotContain("Version", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task SavePreservesOtherKeysAndReplacesExistingKey()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        var store = new DpapiTranslationCredentialStore(path, _protector);
        await store.SaveAsync(TranslationProviderKind.DeepL, "https://one.example", "first", CancellationToken.None);
        await store.SaveAsync(TranslationProviderKind.DeepL, "https://two.example", "second", CancellationToken.None);
        await store.SaveAsync(TranslationProviderKind.DeepL, "https://one.example", "updated", CancellationToken.None);

        Assert.Equal("updated", await store.GetAsync(TranslationProviderKind.DeepL, "https://one.example", CancellationToken.None));
        Assert.Equal("second", await store.GetAsync(TranslationProviderKind.DeepL, "https://two.example", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteRemovesOnlyRequestedKey()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        var store = new DpapiTranslationCredentialStore(path, _protector);
        await store.SaveAsync(TranslationProviderKind.DeepL, "https://one.example", "first", CancellationToken.None);
        await store.SaveAsync(TranslationProviderKind.DeepL, "https://two.example", "second", CancellationToken.None);

        await store.DeleteAsync(TranslationProviderKind.DeepL, "https://one.example", CancellationToken.None);

        Assert.False(await store.HasAsync(TranslationProviderKind.DeepL, "https://one.example", CancellationToken.None));
        Assert.Equal("second", await store.GetAsync(TranslationProviderKind.DeepL, "https://two.example", CancellationToken.None));
    }

    [Fact]
    public async Task CorruptOrForeignCredentialFileReturnsMissing()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        await File.WriteAllTextAsync(path, "corrupt");
        var store = new DpapiTranslationCredentialStore(path, _protector);
        Assert.Null(await store.GetAsync(TranslationProviderKind.MyMemory, "", CancellationToken.None));
        Assert.False(await store.HasAsync(TranslationProviderKind.MyMemory, "", CancellationToken.None));

        var foreignStore = new DpapiTranslationCredentialStore(path, new RejectingProtector());
        Assert.Null(await foreignStore.GetAsync(TranslationProviderKind.MyMemory, "", CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedVersionReturnsMissing()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        await File.WriteAllBytesAsync(path, _protector.Protect(Encoding.UTF8.GetBytes("{\"Version\":99,\"Credentials\":{}}")));
        var store = new DpapiTranslationCredentialStore(path, _protector);

        Assert.Null(await store.GetAsync(TranslationProviderKind.MyMemory, "", CancellationToken.None));
    }

    [Fact]
    public async Task FailedProtectionLeavesExistingEncryptedFileIntact()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        using var initialStore = new DpapiTranslationCredentialStore(path, _protector);
        await initialStore.SaveAsync(TranslationProviderKind.DeepL, "", "initial", CancellationToken.None);
        using var failingStore = new DpapiTranslationCredentialStore(path, new RejectingProtector());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingStore.SaveAsync(TranslationProviderKind.DeepL, "", "secret-to-hide", CancellationToken.None));

        Assert.DoesNotContain("secret-to-hide", failure.ToString());
        Assert.Equal("initial", await initialStore.GetAsync(TranslationProviderKind.DeepL, "", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentSavesOnOneStorePreserveEveryCredential()
    {
        var path = Path.Combine(_root, "translation-secrets.dat");
        using var store = new DpapiTranslationCredentialStore(path, _protector);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            store.SaveAsync(TranslationProviderKind.DeepL, $"https://{index}.example", $"key-{index}", CancellationToken.None)));

        foreach (var index in Enumerable.Range(0, 20))
            Assert.Equal($"key-{index}", await store.GetAsync(TranslationProviderKind.DeepL, $"https://{index}.example", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class XorTestProtector(byte mask) : ITranslationDataProtector
    {
        public byte[] Protect(byte[] data) => data.Select(value => (byte)(value ^ mask)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    private sealed class RejectingProtector : ITranslationDataProtector
    {
        public byte[] Protect(byte[] data) => throw new InvalidOperationException();
        public byte[] Unprotect(byte[] data) => throw new System.Security.Cryptography.CryptographicException("foreign blob");
    }
}
