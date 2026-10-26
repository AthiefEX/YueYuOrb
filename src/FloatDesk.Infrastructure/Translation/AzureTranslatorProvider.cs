using System.Net;
using System.Text;
using System.Text.Json;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

internal static class CustomProviderHttp
{
    internal static Uri Endpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!TranslationEndpointPolicy.TryNormalizeHttps(endpoint.AbsoluteUri, out _, out _))
            throw new TranslationProviderConfigurationException("Endpoint must be a valid HTTPS URL.");
        return endpoint;
    }

    internal static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new TranslationProviderConfigurationException($"{name} is required.") : value;

    internal static TranslationProviderResult? Validate(TranslationRequest request, TranslationProviderCapabilities capabilities)
    {
        if (!TranslationLanguages.IsKnown(request.SourceLanguage) || !TranslationLanguages.IsTranslationLanguage(request.TargetLanguage))
            return TranslationProviderResult.Fail(TranslationFailureKind.UnsupportedLanguage);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > capabilities.MaxCharacters ||
            Encoding.UTF8.GetByteCount(request.Text) > capabilities.MaxUtf8BytesPerRequest)
            return TranslationProviderResult.Fail(TranslationFailureKind.InvalidRequest);
        return null;
    }

    internal static async Task<TranslationProviderResult> SendAsync(HttpClient client, HttpRequestMessage message,
        Func<JsonElement, TranslationProviderResult> parse, CancellationToken cancellationToken)
    {
        try
        {
            using (message)
            using (var response = await client.SendAsync(message, cancellationToken))
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return TranslationProviderResult.Fail(TranslationFailureKind.Authentication);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    return TranslationProviderResult.Fail(TranslationFailureKind.Quota);
                if (!response.IsSuccessStatusCode)
                    return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return parse(document.RootElement);
            }
        }
        catch (JsonException) { return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError); }
        catch (HttpRequestException) { return TranslationProviderResult.Fail(TranslationFailureKind.Network); }
        catch (OperationCanceledException)
        {
            return TranslationProviderResult.Fail(cancellationToken.IsCancellationRequested
                ? TranslationFailureKind.Cancelled : TranslationFailureKind.Timeout);
        }
    }

    internal static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    internal static JsonElement? First(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var array) &&
        array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0] : null;

    internal static TranslationProviderResult Result(string? text, string? detected = null) =>
        string.IsNullOrWhiteSpace(text) ? TranslationProviderResult.Fail(TranslationFailureKind.ProviderError)
            : TranslationProviderResult.Ok(text, detected);

    internal static HttpRequestMessage JsonPost(Uri uri, object payload)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, uri);
        message.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return message;
    }

    internal static Uri AppendPath(Uri baseUri, string path)
    {
        var builder = new UriBuilder(baseUri) { Path = baseUri.AbsolutePath.TrimEnd('/') + path, Query = "" };
        return builder.Uri;
    }
}

public sealed class AzureTranslatorProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string? _region;

    public AzureTranslatorProvider(HttpClient httpClient, Uri endpoint, string apiKey, string? region = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = CustomProviderHttp.Endpoint(endpoint);
        _apiKey = CustomProviderHttp.Required(apiKey, "API key");
        _region = region;
    }

    public string Id => "azure";
    public string DisplayName => "Azure Translator";
    public TranslationProviderCapabilities Capabilities { get; } = new(true, 5000, 20000, 10, 4, TimeSpan.Zero);

    public Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = CustomProviderHttp.Validate(request, Capabilities);
        if (invalid is not null) return Task.FromResult(invalid);
        var to = TranslationLanguageMapper.ToAzure(request.TargetLanguage);
        var uri = CustomProviderHttp.AppendPath(_endpoint, "/translate");
        var builder = new UriBuilder(uri) { Query = "api-version=3.0&to=" + Uri.EscapeDataString(to) };
        if (!request.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
            builder.Query += "&from=" + Uri.EscapeDataString(TranslationLanguageMapper.ToAzure(request.SourceLanguage));
        var message = CustomProviderHttp.JsonPost(builder.Uri, new[] { new { Text = request.Text } });
        message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", _apiKey);
        if (!string.IsNullOrWhiteSpace(_region)) message.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Region", _region);
        return CustomProviderHttp.SendAsync(_http, message, Parse, cancellationToken);
    }

    private static TranslationProviderResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
        var first = root[0];
        if (first.ValueKind != JsonValueKind.Object) return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
        var translation = CustomProviderHttp.First(first, "translations");
        var detected = first.TryGetProperty("detectedLanguage", out var language) ? CustomProviderHttp.String(language, "language") : null;
        return translation is null ? TranslationProviderResult.Fail(TranslationFailureKind.ProviderError)
            : CustomProviderHttp.Result(CustomProviderHttp.String(translation.Value, "text"), detected);
    }
}
