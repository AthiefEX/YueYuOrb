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
    private readonly Dictionary<(string Text, string Source, string Target, TranslationPurpose Purpose), PendingOperation> _pending = [];
    private readonly Timer _cleanupTimer;
    private int _activeOperations;
    private bool _disposed;
    private DateTimeOffset _nextRequest;

    private sealed class PendingOperation
    {
        public TaskCompletionSource<TranslationResponse> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public int Subscribers { get; set; }
        public bool Completed { get; set; }
        public bool Abandoned { get; set; }
    }

    public TranslationCenter(ITranslationProvider provider, IClock clock)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _concurrency = new SemaphoreSlim(Math.Max(1, provider.Capabilities.MaxConcurrentRequests));
        _cleanupTimer = new Timer(_ => PurgeExpired(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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
        PendingOperation operation;
        var start = false;
        lock (_sync)
        {
            if (_disposed) return Task.FromResult(Fail(TranslationFailureKind.Cancelled));
            if (_cache.TryGetValue(key, out var cached))
            {
                if (cached.Expires > _clock.UtcNow) return Task.FromResult(cached.Response);
                _cache.Remove(key);
            }
            if (!_pending.TryGetValue(key, out operation!))
            {
                operation = new PendingOperation();
                _pending[key] = operation;
                _activeOperations++;
                start = true;
            }
            operation.Subscribers++;
        }
        if (start) _ = CompleteAsync(operation, key, normalized, source, target, purpose);
        return AwaitResponseAsync(operation, key, normalized, source, cancellationToken);

        TranslationResponse Fail(TranslationFailureKind kind) =>
            TranslationResponse.Fail(normalized, source, _provider.DisplayName, kind, kind.ToString(), kind.ToString());
    }

    private async Task<TranslationResponse> AwaitResponseAsync(PendingOperation operation,
        (string Text, string Source, string Target, TranslationPurpose Purpose) key,
        string originalText, string sourceLanguage, CancellationToken cancellationToken)
    {
        try { return await operation.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            return TranslationResponse.Fail(originalText, sourceLanguage, _provider.DisplayName,
                TranslationFailureKind.Cancelled, "Cancelled", "Cancelled");
        }
        finally
        {
            var cancel = false;
            lock (_sync)
            {
                operation.Subscribers--;
                if (operation.Subscribers == 0 && !operation.Completed)
                {
                    operation.Abandoned = true;
                    if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                        _pending.Remove(key);
                    cancel = true;
                }
            }
            if (cancel) TryCancel(operation);
        }
    }

    private async Task CompleteAsync(PendingOperation operation,
        (string Text, string Source, string Target, TranslationPurpose Purpose) key,
        string text, string source, string target, TranslationPurpose purpose)
    {
        try
        {
            TranslationResponse response;
            try { response = await ExecuteAsync(text, source, target, purpose, operation.Cancellation.Token); }
            catch (OperationCanceledException)
            {
                response = TranslationResponse.Fail(text, source, _provider.DisplayName,
                    TranslationFailureKind.Cancelled, "Cancelled", "Cancelled");
            }
            catch (Exception)
            {
                response = TranslationResponse.Fail(text, source, _provider.DisplayName,
                    TranslationFailureKind.ProviderError, "ProviderError", "ProviderError");
            }
            lock (_sync)
            {
                if (!_disposed && !operation.Abandoned && !operation.Cancellation.IsCancellationRequested && response.Success)
                {
                    if (_cache.Count >= 128)
                    {
                        var oldest = _cache.MinBy(entry => entry.Value.Expires).Key;
                        _cache.Remove(oldest);
                    }
                    _cache[key] = (response, _clock.UtcNow.AddMinutes(10));
                }
                if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
                    _pending.Remove(key);
                operation.Completed = true;
            }
            operation.Completion.TrySetResult(response);
        }
        finally
        {
            operation.Cancellation.Dispose();
            lock (_sync)
            {
                _activeOperations--;
                if (_disposed && _activeOperations == 0) DisposeGates();
            }
        }
    }

    public int PurgeExpired()
    {
        lock (_sync)
        {
            var expired = _cache.Where(entry => entry.Value.Expires <= _clock.UtcNow)
                .Select(entry => entry.Key).ToArray();
            foreach (var key in expired) _cache.Remove(key);
            return expired.Length;
        }
    }

    public void Dispose()
    {
        PendingOperation[] operations;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            operations = _pending.Values.ToArray();
            _pending.Clear();
            if (_activeOperations == 0) DisposeGates();
        }
        _cleanupTimer.Dispose();
        foreach (var operation in operations) TryCancel(operation);
    }

    private static void TryCancel(PendingOperation operation)
    {
        try { operation.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void DisposeGates()
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
        var requestSource = source.Equals(TranslationLanguages.Auto, StringComparison.OrdinalIgnoreCase) && !capabilities.SupportsAutoDetect
            ? TranslationLanguageDetector.Detect(text).LanguageTag : source;
        if (requestSource.Equals(target, StringComparison.OrdinalIgnoreCase)) return Fail(TranslationFailureKind.NoTranslationNeeded);
        var reportedSource = requestSource;

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

                var result = await _provider.TranslateAsync(new TranslationRequest(chunk.Text, requestSource, target, purpose), cancellationToken);
                if (!result.Success) return Fail(result.FailureKind);
                if (result.TranslatedText is null) return Fail(TranslationFailureKind.ProviderError);
                translated.Add(result.TranslatedText + chunk.SeparatorAfter);
                if (result.DetectedLanguage is { Length: > 0 } detected && TranslationLanguages.IsKnown(detected))
                    reportedSource = detected;
            }
            catch (OperationCanceledException) { return Fail(TranslationFailureKind.Cancelled); }
            catch (TimeoutException) { return Fail(TranslationFailureKind.Timeout); }
            catch (Exception) { return Fail(TranslationFailureKind.ProviderError); }
            finally { _concurrency.Release(); }
        }
        return TranslationResponse.Ok(text, TranslationTextProcessor.Join(translated, target), reportedSource, _provider.DisplayName);
    }
}
