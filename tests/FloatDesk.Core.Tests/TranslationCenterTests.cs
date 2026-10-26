using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using System.Collections;
using System.Reflection;

namespace FloatDesk.Core.Tests;

public sealed class TranslationCenterTests
{
    [Fact]
    public async Task IdenticalRequestsShareOneProviderCallAndCacheResult()
    {
        var provider = new BlockedProvider("你好");
        var center = new TranslationCenter(provider, new ManualClock());
        var first = center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, provider.CallCount);
        provider.Release();
        await Task.WhenAll(first, second);
        var third = await center.TranslateAsync("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("你好", third.TranslatedText);
    }

    [Fact]
    public async Task CancellingOneSubscriberPreservesSharedProviderWork()
    {
        var provider = new BlockedProvider("Bonjour");
        using var center = new TranslationCenter(provider, new ManualClock());
        using var firstCancellation = new CancellationTokenSource();
        var first = center.TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, firstCancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = center.TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, default);
        firstCancellation.Cancel();
        Assert.Equal(TranslationFailureKind.Cancelled, (await first).FailureKind);
        Assert.False(provider.WasCancelled);
        provider.Release();
        Assert.Equal("Bonjour", (await second).TranslatedText);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task CancellingLastSubscriberStopsChunksAndDoesNotCache()
    {
        var provider = new BlockedProvider("X", maxCharacters: 5);
        using var center = new TranslationCenter(provider, new ManualClock());
        using var cancellation = new CancellationTokenSource();
        var first = center.TranslateAsync("Hello world", "en", "fr", TranslationPurpose.Interactive, cancellation.Token);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(TranslationFailureKind.Cancelled, (await first).FailureKind);
        Assert.True(provider.LastToken.CanBeCanceled);
        Assert.True(provider.LastToken.IsCancellationRequested);
        Assert.Equal(1, provider.CallCount);
        provider.Release();
        var retry = await center.TranslateAsync("Hello world", "en", "fr", TranslationPurpose.Interactive, default);
        Assert.True(retry.Success);
        Assert.Equal(4, provider.CallCount);
    }

    [Fact]
    public async Task NativeAutoDetectionKeepsAutoForEveryChunk()
    {
        var provider = new FakeProvider("X")
        {
            MaxCharacters = 5,
            Result = TranslationProviderResult.Ok("X", "en")
        };
        var result = await new TranslationCenter(provider, new ManualClock())
            .TranslateAsync("Hello world", "auto", "fr", TranslationPurpose.Interactive, default);
        Assert.True(result.Success);
        Assert.Equal("en", result.SourceLanguage);
        Assert.Equal(["auto", "auto", "auto"], provider.Requests.Select(x => x.SourceLanguage).ToArray());
    }

    [Fact]
    public async Task ExpiredEntriesCanBePurgedWithoutAnotherTranslation()
    {
        var clock = new ManualClock();
        using var center = new TranslationCenter(new FakeProvider("Bonjour"), clock);
        await center.TranslateAsync("Hello", "en", "fr", TranslationPurpose.Interactive, default);
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, center.PurgeExpired());
        Assert.Equal(0, RetainedCacheEntries(center));
    }

    [Fact]
    public async Task DisposeClearsCachedTextAndCancelsPendingWork()
    {
        var provider = new BlockedProvider("Bonjour");
        var center = new TranslationCenter(provider, new ManualClock());
        var completed = center.TranslateAsync("Cached", "en", "fr", TranslationPurpose.Interactive, default);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.Release();
        await completed;
        Assert.Equal(1, RetainedCacheEntries(center));
        var pendingProvider = new BlockedProvider("X");
        var pendingCenter = new TranslationCenter(pendingProvider, new ManualClock());
        var pending = pendingCenter.TranslateAsync("Pending", "en", "fr", TranslationPurpose.Interactive, default);
        await pendingProvider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        center.Dispose();
        pendingCenter.Dispose();
        Assert.True(pendingProvider.LastToken.CanBeCanceled);
        Assert.True(pendingProvider.LastToken.IsCancellationRequested);
        Assert.Equal(0, RetainedCacheEntries(center));
        Assert.Equal(0, RetainedCacheEntries(pendingCenter));
        Assert.Equal(0, RetainedPendingEntries(pendingCenter));
        Assert.Equal(TranslationFailureKind.Cancelled, (await pending).FailureKind);
    }

    private static int RetainedCacheEntries(TranslationCenter center) =>
        ((IDictionary)typeof(TranslationCenter).GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(center)!).Count;

    private static int RetainedPendingEntries(TranslationCenter center) =>
        ((IDictionary)typeof(TranslationCenter).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(center)!).Count;

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

    private sealed class BlockedProvider(string translation, int maxCharacters = 1000) : ITranslationProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "blocked";
        public string DisplayName => "Blocked";
        public int CallCount => Volatile.Read(ref _calls);
        public bool WasCancelled => LastToken.IsCancellationRequested;
        public CancellationToken LastToken { get; private set; }
        public TranslationProviderCapabilities Capabilities => new(true, maxCharacters, 10000, 10, 1, TimeSpan.Zero);
        public void Release() => _release.TrySetResult();
        public async Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastToken = cancellationToken;
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return TranslationProviderResult.Ok(translation);
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
