using FloatDesk.Core.Contracts;

namespace FloatDesk.Core.Translation;

public sealed class TranslationCenter : IDisposable
{
    private readonly ITranslationProvider _provider;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<(string Text, string Source, string Target, TranslationPurpose Purpose),
        (TranslationResponse Response, DateTimeOffset Expires)> _cache = [];
    private readonly Dictionary<(string Text, string Source, string Target, TranslationPurpose Purpose), Task<TranslationResponse>> _pending = [];
    private DateTimeOffset _nextRequest;

    public TranslationCenter(ITranslationProvider provider, IClock clock)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _concurrency = new SemaphoreSlim(Math.Max(1, provider.Capabilities.MaxConcurrentRequests));
    }

    public Task<TranslationResponse> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
        TranslationPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = TranslationTextProcessor.Normalize(text);
        var source = sourceLanguage?.Trim() ?? "";
        var target = targetLanguage?.Trim() ?? "";
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(Fail(TranslationFailureKind.Cancelled));
        if (normalized.Length == 0)
            return Task.FromResult(Fail(TranslationFailureKind.EmptyText));
        if (!TranslationLanguages.IsKnown(source) || !TranslationLanguages.IsKnown(target) ||
            target.Equals(TranslationLanguages.Auto, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail(TranslationFailureKind.UnsupportedLanguage));
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail(TranslationFailureKind.NoTranslationNeeded));

        var key = (normalized, source, target, purpose);
        Task<TranslationResponse> task;
        TaskCompletionSource<TranslationResponse>? completion = null;
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Expires > _clock.UtcNow) return Task.FromResult(cached.Response);
                _cache.Remove(key);
            }
            if (!_pending.TryGetValue(key, out task!))
            {
                completion = new TaskCompletionSource<TranslationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                _pending[key] = task;
            }
        }
        if (completion is not null)
            _ = CompleteAsync(completion, key, normalized, source, target, purpose);
        return AwaitResponseAsync(task, normalized, source, cancellationToken);

        TranslationResponse Fail(TranslationFailureKind kind) =>
            TranslationResponse.Fail(normalized, source, _provider.DisplayName, kind, kind.ToString(), kind.ToString());
    }

    private async Task<TranslationResponse> AwaitResponseAsync(Task<TranslationResponse> task,
        string originalText, string sourceLanguage, CancellationToken cancellationToken)
    {
        try { return await task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            return TranslationResponse.Fail(originalText, sourceLanguage, _provider.DisplayName,
                TranslationFailureKind.Cancelled, "Cancelled", "Cancelled");
        }
    }

    private async Task CompleteAsync(TaskCompletionSource<TranslationResponse> completion,
        (string Text, string Source, string Target, TranslationPurpose Purpose) key,
        string text, string source, string target, TranslationPurpose purpose)
    {
        try
        {
            var response = await ExecuteAsync(text, source, target, purpose, CancellationToken.None);
            lock (_sync)
            {
                if (response.Success)
                {
                    if (_cache.Count >= 128)
                    {
                        var oldest = _cache.MinBy(entry => entry.Value.Expires).Key;
                        _cache.Remove(oldest);
                    }
                    _cache[key] = (response, _clock.UtcNow.AddMinutes(10));
                }
                _pending.Remove(key);
            }
            completion.SetResult(response);
        }
        catch (Exception exception)
        {
            lock (_sync) _pending.Remove(key);
            completion.SetException(exception);
        }
    }

    public void Dispose()
    {
        _concurrency.Dispose();
        _rateGate.Dispose();
    }

    private async Task<TranslationResponse> ExecuteAsync(string text, string source, string target,
        TranslationPurpose purpose, CancellationToken cancellationToken)
    {
        TranslationResponse Fail(TranslationFailureKind kind) =>
            TranslationResponse.Fail(text, source, _provider.DisplayName, kind, kind.ToString(), kind.ToString());
        var capabilities = _provider.Capabilities;
        if (capabilities.MaxCharacters <= 0 || capabilities.MaxUtf8BytesPerRequest <= 0 || capabilities.MaxChunks <= 0)
            return Fail(TranslationFailureKind.InvalidRequest);
        IReadOnlyList<TranslationTextChunk> chunks;
        try { chunks = TranslationTextProcessor.Chunk(text, capabilities.MaxUtf8BytesPerRequest, capabilities.MaxCharacters); }
        catch (ArgumentException) { return Fail(TranslationFailureKind.InvalidRequest); }
        if (chunks.Count > capabilities.MaxChunks) return Fail(TranslationFailureKind.InvalidRequest);
        var resolved = source.Equals(TranslationLanguages.Auto, StringComparison.OrdinalIgnoreCase) && !capabilities.SupportsAutoDetect
            ? TranslationLanguageDetector.Detect(text).LanguageTag : source;
        if (resolved.Equals(target, StringComparison.OrdinalIgnoreCase)) return Fail(TranslationFailureKind.NoTranslationNeeded);

        var translated = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            await _concurrency.WaitAsync(cancellationToken);
            try
            {
                await _rateGate.WaitAsync(cancellationToken);
                try
                {
                    var remaining = _nextRequest - _clock.UtcNow;
                    if (remaining > TimeSpan.Zero) await _clock.DelayAsync(remaining, cancellationToken);
                    _nextRequest = _clock.UtcNow + capabilities.MinimumRequestInterval;
                }
                finally { _rateGate.Release(); }

                var result = await _provider.TranslateAsync(new TranslationRequest(chunk.Text, resolved, target, purpose), cancellationToken);
                if (!result.Success) return Fail(result.FailureKind);
                if (result.TranslatedText is null) return Fail(TranslationFailureKind.ProviderError);
                translated.Add(result.TranslatedText + chunk.SeparatorAfter);
                if (result.DetectedLanguage is { Length: > 0 } detected && TranslationLanguages.IsKnown(detected))
                    resolved = detected;
            }
            catch (OperationCanceledException) { return Fail(TranslationFailureKind.Cancelled); }
            catch (TimeoutException) { return Fail(TranslationFailureKind.Timeout); }
            catch (Exception) { return Fail(TranslationFailureKind.ProviderError); }
            finally { _concurrency.Release(); }
        }
        return TranslationResponse.Ok(text, TranslationTextProcessor.Join(translated, target), resolved, _provider.DisplayName);
    }
}
