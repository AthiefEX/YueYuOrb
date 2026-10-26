using System.Text.Json;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public sealed class LibreTranslateProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string? _apiKey;

    public LibreTranslateProvider(HttpClient httpClient, Uri endpoint, string? apiKey)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = CustomProviderHttp.Endpoint(endpoint);
        _apiKey = apiKey;
    }

    public string Id => "libretranslate";
    public string DisplayName => "LibreTranslate";
    public TranslationProviderCapabilities Capabilities { get; } = new(true, 5000, 20000, 10, 4, TimeSpan.Zero);

    public Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = CustomProviderHttp.Validate(request, Capabilities);
        if (invalid is not null) return Task.FromResult(invalid);
        var payload = new Dictionary<string, string>
        {
            ["q"] = request.Text,
            ["source"] = request.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto" : TranslationLanguageMapper.ToLibre(request.SourceLanguage),
            ["target"] = TranslationLanguageMapper.ToLibre(request.TargetLanguage),
            ["format"] = "text"
        };
        if (!string.IsNullOrWhiteSpace(_apiKey)) payload["api_key"] = _apiKey;
        var message = CustomProviderHttp.JsonPost(CustomProviderHttp.AppendPath(_endpoint, "/translate"), payload);
        return CustomProviderHttp.SendAsync(_http, message, Parse, cancellationToken);
    }

    private static TranslationProviderResult Parse(JsonElement root)
    {
        var detected = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detectedLanguage", out var value)
            ? CustomProviderHttp.String(value, "language") : null;
        return CustomProviderHttp.Result(CustomProviderHttp.String(root, "translatedText"), detected);
    }
}
