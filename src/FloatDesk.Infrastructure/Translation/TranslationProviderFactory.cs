using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public static class TranslationEndpointDefaults
{
    public static string For(TranslationProviderKind kind) => kind switch
    {
        TranslationProviderKind.MyMemory => "https://api.mymemory.translated.net/get",
        TranslationProviderKind.AzureTranslator => "https://api.cognitive.microsofttranslator.com",
        TranslationProviderKind.DeepL => "https://api-free.deepl.com",
        _ => throw new TranslationProviderConfigurationException($"Provider {kind} requires an endpoint.")
    };
}

public sealed class TranslationProviderFactory : ITranslationProviderFactory, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public TranslationProviderFactory(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
    }

    public ITranslationProvider Create(TranslationSettings settings, string? apiKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Provider == TranslationProviderKind.MyMemory)
            return new MyMemoryTranslationProvider(_httpClient);

        var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? settings.Provider switch
            {
                TranslationProviderKind.AzureTranslator or TranslationProviderKind.DeepL => TranslationEndpointDefaults.For(settings.Provider),
                _ => throw new TranslationProviderConfigurationException("Endpoint is required.")
            }
            : settings.Endpoint;
        if (!TranslationEndpointPolicy.TryNormalizeHttps(endpoint, out var normalized, out _))
            throw new TranslationProviderConfigurationException("Endpoint must be a valid HTTPS URL.");
        var uri = new Uri(normalized!);
        return settings.Provider switch
        {
            TranslationProviderKind.AzureTranslator => new AzureTranslatorProvider(_httpClient, uri, CustomProviderHttp.Required(apiKey, "API key"), settings.AzureRegion),
            TranslationProviderKind.DeepL => new DeepLTranslationProvider(_httpClient, uri, CustomProviderHttp.Required(apiKey, "API key")),
            TranslationProviderKind.LibreTranslate => new LibreTranslateProvider(_httpClient, uri, apiKey),
            TranslationProviderKind.OpenAiCompatible => new OpenAiCompatibleTranslationProvider(_httpClient, uri, settings.Model, CustomProviderHttp.Required(apiKey, "API key")),
            _ => throw new TranslationProviderConfigurationException("Unknown translation provider.")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
