using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public static class TranslationEndpointDefaults
{
    public static string For(TranslationProviderKind kind) => kind switch
    {
        TranslationProviderKind.MyMemory => "https://api.mymemory.translated.net/get",
        _ => throw new TranslationProviderConfigurationException($"Provider {kind} is not implemented.")
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
        return settings.Provider switch
        {
            TranslationProviderKind.MyMemory => new MyMemoryTranslationProvider(_httpClient),
            _ => throw new TranslationProviderConfigurationException($"Provider {settings.Provider} is not implemented.")
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
