using System.Net;
using System.Text.Json;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public sealed class MyMemoryTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;

    public MyMemoryTranslationProvider(HttpClient httpClient, Uri? endpoint = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = endpoint ?? new Uri(TranslationEndpointDefaults.For(FloatDesk.Core.Models.TranslationProviderKind.MyMemory));
        if (!TranslationEndpointPolicy.TryNormalizeHttps(_endpoint.AbsoluteUri, out _, out _))
            throw new ArgumentException("MyMemory endpoint must be an HTTPS URL.", nameof(endpoint));
    }

    public string Id => "mymemory";
    public string DisplayName => "MyMemory";
    public TranslationProviderCapabilities Capabilities { get; } =
        new(false, 5000, 450, 10, 1, TimeSpan.FromSeconds(1));

    public async Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string source;
        string target;
        try
        {
            source = TranslationLanguageMapper.ToMyMemory(request.SourceLanguage);
            target = TranslationLanguageMapper.ToMyMemory(request.TargetLanguage);
        }
        catch (ArgumentException)
        {
            return TranslationProviderResult.Fail(TranslationFailureKind.UnsupportedLanguage);
        }

        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > Capabilities.MaxCharacters ||
            System.Text.Encoding.UTF8.GetByteCount(request.Text) > Capabilities.MaxUtf8BytesPerRequest)
            return TranslationProviderResult.Fail(TranslationFailureKind.InvalidRequest);

        var builder = new UriBuilder(_endpoint);
        builder.Query = $"q={Uri.EscapeDataString(request.Text)}&langpair={Uri.EscapeDataString($"{source}|{target}")}";
        try
        {
            using var response = await _httpClient.GetAsync(builder.Uri, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return TranslationProviderResult.Fail(TranslationFailureKind.Quota);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TranslationProviderResult.Fail(TranslationFailureKind.Authentication);
            if (!response.IsSuccessStatusCode)
                return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);

            var status = root.TryGetProperty("responseStatus", out var statusElement) &&
                         statusElement.ValueKind == JsonValueKind.Number && statusElement.TryGetInt32(out var value)
                ? value : 0;
            var details = ReadString(root, "responseDetails");
            var translated = root.TryGetProperty("responseData", out var data) && data.ValueKind == JsonValueKind.Object
                ? ReadString(data, "translatedText") : null;

            var providerWarning = translated?.TrimStart().StartsWith("MYMEMORY WARNING:", StringComparison.OrdinalIgnoreCase) == true;
            if (status == 429 || IsQuota(details) || (providerWarning && IsQuota(translated)))
                return TranslationProviderResult.Fail(TranslationFailureKind.Quota);
            if (status != 200 || string.IsNullOrWhiteSpace(translated) || IsWarning(details) || providerWarning)
                return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
            return TranslationProviderResult.Ok(WebUtility.HtmlDecode(translated));
        }
        catch (JsonException)
        {
            return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
        }
        catch (HttpRequestException)
        {
            return TranslationProviderResult.Fail(TranslationFailureKind.Network);
        }
        catch (OperationCanceledException)
        {
            return TranslationProviderResult.Fail(cancellationToken.IsCancellationRequested
                ? TranslationFailureKind.Cancelled : TranslationFailureKind.Timeout);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool IsWarning(string? text) =>
        text?.Contains("WARNING", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsQuota(string? text) =>
        text?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true ||
        text?.Contains("used all available free translations", StringComparison.OrdinalIgnoreCase) == true;
}
