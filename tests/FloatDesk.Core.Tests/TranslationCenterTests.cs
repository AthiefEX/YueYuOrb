using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Core.Tests;

public sealed class TranslationCenterTests
{
    [Fact]
    public async Task IdenticalRequestsShareOneProviderCallAndCacheResult()
    {
        var provider = new FakeProvider("你好");
        var center = new TranslationCenter(provider, new ManualClock());
        var first = center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        var second = center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        await Task.WhenAll(first, second);
        var third = await center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("你好", third.TranslatedText);
    }

    [Fact]
    public async Task NonAutoProviderReceivesLocallyResolvedLanguage()
    {
        var provider = new FakeProvider("你好") { SupportsAutoDetect = false };
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello world", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("en", Assert.Single(provider.Requests).SourceLanguage);
    }

    [Fact]
    public async Task TextAlreadyInTargetLanguageDoesNotCallProvider()
    {
        var provider = new FakeProvider("unused");
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("这是中文", "zh-Hans", "zh-Hans", TranslationPurpose.Interactive, default);
        Assert.Equal(TranslationFailureKind.NoTranslationNeeded, result.FailureKind);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ChunkingPreservesOrderAndLimit()
    {
        var provider = new FakeProvider("X") { MaxCharacters = 5 };
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello world", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.True(result.Success);
        Assert.Equal("X X X", result.TranslatedText);
        Assert.Equal(["Hello", " worl", "d"], provider.Requests.Select(x => x.Text).ToArray());
    }

    [Fact]
    public async Task CacheExpiresAfterTenMinutes()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider("Bonjour");
        var center = new TranslationCenter(provider, clock);
        await center.TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, default);
        clock.Advance(TimeSpan.FromMinutes(11));
        await center.TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.Equal(2, provider.CallCount);
    }

    [Fact]
    public async Task CacheEvictsOldestAfter128Entries()
    {
        var provider = new FakeProvider("X");
        var center = new TranslationCenter(provider, new ManualClock());
        for (var i = 0; i < 129; i++)
            await center.TranslateAsync($"Hello {i}", "en", "fr", TranslationPurpose.Interactive, default);
        await center.TranslateAsync("Hello 0", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.Equal(130, provider.CallCount);
    }

    [Fact]
    public async Task MinimumIntervalUsesClockDelay()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider("X") { MinimumInterval = TimeSpan.FromSeconds(2) };
        var center = new TranslationCenter(provider, clock);
        await center.TranslateAsync("Hello 1", "en", "fr", TranslationPurpose.Interactive, default);
        await center.TranslateAsync("Hello 2", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(clock.Delays));
    }

    [Fact]
    public async Task ExcessiveChunksAreRejectedBeforeProviderCall()
    {
        var provider = new FakeProvider("X") { MaxCharacters = 5, MaxChunks = 1 };
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello world", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.Equal(TranslationFailureKind.InvalidRequest, result.FailureKind);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task CancelledRequestReturnsTypedFailureWithoutProviderCall()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new FakeProvider("X");
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, cancellation.Token);
        Assert.Equal(TranslationFailureKind.Cancelled, result.FailureKind);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ProviderFailureDoesNotExposeSourceText()
    {
        var provider = new FakeProvider("unused") { Result = TranslationProviderResult.Fail(TranslationFailureKind.Network, "network", "Hello secret") };
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello secret", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.Equal(TranslationFailureKind.Network, result.FailureKind);
        Assert.DoesNotContain("Hello secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public void CredentialKeyNormalizesEndpointAndIsSha256Hex()
    {
        var first = TranslationCredentialKey.For(TranslationProviderKind.DeepL, " HTTPS://EXAMPLE.COM/path ");
        var second = TranslationCredentialKey.For(TranslationProviderKind.DeepL, "https://example.com/path");
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    private sealed class FakeProvider(string translation) : ITranslationProvider
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool SupportsAutoDetect { get; init; } = true;
        public int MaxCharacters { get; init; } = 1000;
        public int MaxChunks { get; init; } = 10;
        public TimeSpan MinimumInterval { get; init; }
        public TranslationProviderResult? Result { get; init; }
        public List<TranslationRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;
        public TranslationProviderCapabilities Capabilities => new(SupportsAutoDetect, MaxCharacters, 10000, MaxChunks, 2, MinimumInterval);
        public Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Result ?? TranslationProviderResult.Ok(translation));
        }
    }

    private sealed class ManualClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
        public List<TimeSpan> Delays { get; } = [];
        public void Advance(TimeSpan amount) => UtcNow += amount;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }
}
