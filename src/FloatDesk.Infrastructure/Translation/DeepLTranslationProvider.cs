using System.Text.Json;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;

    public DeepLTranslationProvider(HttpClient httpClient, Uri endpoint, string apiKey)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = CustomProviderHttp.Endpoint(endpoint);
        _apiKey = CustomProviderHttp.Required(apiKey, "API key");
    }

    public string Id => "deepl";
    public string DisplayName => "DeepL";
    public TranslationProviderCapabilities Capabilities { get; } = new(true, 5000, 20000, 10, 4, TimeSpan.Zero);

    public Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = CustomProviderHttp.Validate(request, Capabilities);
        if (invalid is not null) return Task.FromResult(invalid);
        object body = request.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? new { text = new[] { request.Text }, target_lang = TranslationLanguageMapper.ToDeepL(request.TargetLanguage) }
            : new { text = new[] { request.Text }, target_lang = TranslationLanguageMapper.ToDeepL(request.TargetLanguage), source_lang = TranslationLanguageMapper.ToDeepLSource(request.SourceLanguage) };
        var message = CustomProviderHttp.JsonPost(CustomProviderHttp.AppendPath(_endpoint, "/v2/translate"), body);
        message.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _apiKey);
        return CustomProviderHttp.SendAsync(_http, message, Parse, cancellationToken);
    }

    private static TranslationProviderResult Parse(JsonElement root)
    {
        var first = CustomProviderHttp.First(root, "translations");
        return first is null ? TranslationProviderResult.Fail(TranslationFailureKind.ProviderError)
            : CustomProviderHttp.Result(CustomProviderHttp.String(first.Value, "text"),
                CustomProviderHttp.String(first.Value, "detected_source_language")?.ToLowerInvariant());
    }
}
