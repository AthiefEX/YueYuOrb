using FloatDesk.Core.Models;

namespace FloatDesk.Core.Translation;

public enum TranslationPurpose { Interactive, Subtitle }
public enum TranslationFailureKind
{
    None, EmptyText, NoTranslationNeeded, UnsupportedLanguage, Authentication, Quota,
    Timeout, Network, InvalidRequest, ProviderError, Cancelled
}

public sealed record TranslationRequest(string Text, string SourceLanguage, string TargetLanguage, TranslationPurpose Purpose);
public sealed record TranslationProviderCapabilities(
    bool SupportsAutoDetect, int MaxCharacters, int MaxUtf8BytesPerRequest, int MaxChunks,
    int MaxConcurrentRequests, TimeSpan MinimumRequestInterval);

public sealed record TranslationProviderResult(
    string? TranslatedText, string? DetectedLanguage, TranslationFailureKind FailureKind,
    string? ErrorCode, string? Message)
{
    public bool Success => FailureKind == TranslationFailureKind.None;
    public static TranslationProviderResult Ok(string translatedText, string? detectedLanguage = null) =>
        new(translatedText, detectedLanguage, TranslationFailureKind.None, null, null);
    public static TranslationProviderResult Fail(TranslationFailureKind kind, string? errorCode = null, string? message = null) =>
        new(null, null, kind, errorCode, message);
}

public sealed record TranslationResponse(
    string OriginalText, string? TranslatedText, string SourceLanguage, string ProviderName,
    TranslationFailureKind FailureKind, string? ErrorCode, string? Message)
{
    public bool Success => FailureKind == TranslationFailureKind.None;
    public string? ErrorMessage => Message;
    public static TranslationResponse Ok(string originalText, string translatedText, string sourceLanguage, string providerName) =>
        new(originalText, translatedText, sourceLanguage, providerName, TranslationFailureKind.None, null, null);
    public static TranslationResponse Fail(string originalText, string sourceLanguage, string providerName,
        TranslationFailureKind kind, string? errorCode = null, string? message = null) =>
        new(originalText, null, sourceLanguage, providerName, kind, errorCode, message);
}

public interface ITranslationProvider
{
    string Id { get; }
    string DisplayName { get; }
    TranslationProviderCapabilities Capabilities { get; }
    Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

public interface ITranslationProviderFactory
{
    ITranslationProvider Create(TranslationSettings settings, string? apiKey);
}

public interface ITranslationCredentialStore
{
    Task<bool> HasAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken);
    Task<string?> GetAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken);
    Task SaveAsync(TranslationProviderKind provider, string endpoint, string apiKey, CancellationToken cancellationToken);
    Task DeleteAsync(TranslationProviderKind provider, string endpoint, CancellationToken cancellationToken);
}

public sealed class TranslationProviderConfigurationException : Exception
{
    public TranslationProviderConfigurationException(string message) : base(message) { }
    public TranslationProviderConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}
