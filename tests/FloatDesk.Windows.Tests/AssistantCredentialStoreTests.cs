using FloatDesk.Windows.Assistant;

namespace FloatDesk.Windows.Tests;

public sealed class AssistantCredentialStoreTests
{
    [Fact]
    public async Task MultipleKeysOnSameEndpointRemainIndependentAfterReloadAndDelete()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYu-Keys-" + Guid.NewGuid().ToString("N"));
        var first = "profile:" + Guid.NewGuid().ToString("N"); var second = "profile:" + Guid.NewGuid().ToString("N");
        try
        {
            using (var store = new AssistantCredentialStore(Path.Combine(directory, "keys.dat")))
            { await store.SaveAsync(first, "https://same.test/v1", "first-secret", CancellationToken.None); await store.SaveAsync(second, "https://same.test/v1", "second-secret", CancellationToken.None); }
            using var read = new AssistantCredentialStore(Path.Combine(directory, "keys.dat"));
            Assert.Equal("first-secret", await read.GetAsync(first, "https://same.test/v1", CancellationToken.None));
            Assert.Equal("second-secret", await read.GetAsync(second, "https://same.test/v1", CancellationToken.None));
            await read.DeleteAsync(first, "https://same.test/v1", CancellationToken.None);
            Assert.Null(await read.GetAsync(first, "https://same.test/v1", CancellationToken.None));
            Assert.Equal("second-secret", await read.GetAsync(second, "https://same.test/v1", CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CredentialsAreProtectedAndBoundToPurposeAndEndpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYu-Assistant-Key-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantCredentialStore(Path.Combine(directory, "secrets.dat"));
            await store.SaveAsync("chat", "https://example.test/v1", "ordinary-test-secret", CancellationToken.None);
            Assert.Equal("ordinary-test-secret", await store.GetAsync("chat", "https://example.test/v1/", CancellationToken.None));
            Assert.Null(await store.GetAsync("asr", "https://example.test/v1", CancellationToken.None));
            Assert.Null(await store.GetAsync("chat", "https://other.test/v1", CancellationToken.None));
            Assert.DoesNotContain("ordinary-test-secret", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(directory, "secrets.dat"))));
            await store.DeleteAsync("chat", "https://example.test/v1", CancellationToken.None);
            Assert.Null(await store.GetAsync("chat", "https://example.test/v1", CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
