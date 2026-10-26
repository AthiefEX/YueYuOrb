using System.Text;
using System.Text.Json;
using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public sealed class OpenAiCompatibleTranslationProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string _apiKey;

    public OpenAiCompatibleTranslationProvider(HttpClient httpClient, Uri endpoint, string model, string apiKey)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = CustomProviderHttp.Endpoint(endpoint);
        _model = CustomProviderHttp.Required(model, "Model");
        _apiKey = CustomProviderHttp.Required(apiKey, "API key");
    }

    public string Id => "openai-compatible";
    public string DisplayName => "OpenAI-compatible";
    public TranslationProviderCapabilities Capabilities { get; } = new(true, 5000, 20000, 10, 2, TimeSpan.Zero);

    public Task<TranslationProviderResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invalid = CustomProviderHttp.Validate(request, Capabilities);
        if (invalid is not null) return Task.FromResult(invalid);
        var source = request.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "the source language" : request.SourceLanguage;
        var message = CustomProviderHttp.JsonPost(_endpoint, new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = "Translate the supplied text into the requested target language. Return only the translation, with no explanation." },
                new { role = "user", content = "Source language: " + source + "; target language: " + request.TargetLanguage + ". Text: " + request.Text }
            },
            temperature = 0,
            stream = false
        });
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _apiKey);
        return CustomProviderHttp.SendAsync(_http, message, Parse, cancellationToken);
    }

    private static string? RemoveReasoningBlocks(string? content)
    {
        if (content is null) return null;
        const string open = "<think>";
        const string close = "</think>";
        var remaining = content.AsSpan();
        var translated = new StringBuilder(content.Length);
        while (true)
        {
            var start = remaining.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                translated.Append(remaining);
                break;
            }
            translated.Append(remaining[..start]);
            remaining = remaining[(start + open.Length)..];
            var end = remaining.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                translated.Append(open);
                translated.Append(remaining);
                break;
            }
            remaining = remaining[(end + close.Length)..];
        }
        return translated.ToString().Trim();
    }

    private static TranslationProviderResult Parse(JsonElement root)
    {
        var first = CustomProviderHttp.First(root, "choices");
        if (first is null || first.Value.ValueKind != JsonValueKind.Object || !first.Value.TryGetProperty("message", out var message))
            return TranslationProviderResult.Fail(TranslationFailureKind.ProviderError);
        return CustomProviderHttp.Result(RemoveReasoningBlocks(CustomProviderHttp.String(message, "content")));
    }
}
