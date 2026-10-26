using System.Net;
using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Infrastructure.Translation;

namespace FloatDesk.Infrastructure.Tests;

public sealed class CustomTranslationProviderTests
{
    private const string Secret = "private-key-123";
    private const string Input = "private request text";
    private static readonly TranslationRequest Auto = new(Input, "auto", "zh-Hans", TranslationPurpose.Interactive);

    [Fact]
    public async Task AzureUsesHeadersPathAndOmitsAutoSource()
    {
        var handler = new RecordingHandler("""[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好","to":"zh-Hans"}]}]""");
        using var http = new HttpClient(handler);
        var result = await new AzureTranslatorProvider(http, new Uri("https://example.com"), Secret, "eastasia").TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal("/translate?api-version=3.0&to=zh-Hans", handler.Path);
        Assert.Equal(Secret, handler.Headers!["Ocp-Apim-Subscription-Key"]);
        Assert.Equal("eastasia", handler.Headers["Ocp-Apim-Subscription-Region"]);
        Assert.DoesNotContain(Secret, handler.Body);
        Assert.Contains(Input, handler.Body);
    }

    [Fact]
    public async Task DeepLUsesAuthHeaderMappedTargetAndOmitsAutoSource()
    {
        var handler = new RecordingHandler("""{"translations":[{"detected_source_language":"EN","text":"你好"}]}""");
        using var http = new HttpClient(handler);
        var result = await new DeepLTranslationProvider(http, new Uri("https://example.com"), Secret).TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("en", result.DetectedLanguage);
        Assert.Equal("/v2/translate", handler.Path);
        Assert.Equal("DeepL-Auth-Key " + Secret, handler.Headers!["Authorization"]);
        Assert.Equal("ZH-HANS", JsonDocument.Parse(handler.Body!).RootElement.GetProperty("target_lang").GetString());
        Assert.False(JsonDocument.Parse(handler.Body!).RootElement.TryGetProperty("source_lang", out _));
        Assert.DoesNotContain(Secret, handler.Body);
    }

    [Fact]
    public async Task LibreSendsOptionalKeyInJsonBodyWithoutAuthorizationHeader()
    {
        var handler = new RecordingHandler("""{"translatedText":"你好","detectedLanguage":{"language":"en"}}""");
        using var http = new HttpClient(handler);
        var result = await new LibreTranslateProvider(http, new Uri("https://example.com"), Secret).TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("/translate", handler.Path);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("auto", body.GetProperty("source").GetString());
        Assert.Equal("zh", body.GetProperty("target").GetString());
        Assert.Equal(Input, body.GetProperty("q").GetString());
        Assert.Equal(Secret, body.GetProperty("api_key").GetString());
        Assert.False(handler.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task LibreOmitsApiKeyWhenNotConfigured()
    {
        var handler = new RecordingHandler("""{"translatedText":"你好"}""");
        using var http = new HttpClient(handler);
        var result = await new LibreTranslateProvider(http, new Uri("https://example.com"), " ").TranslateAsync(Auto, CancellationToken.None);
        Assert.True(result.Success);
        Assert.False(JsonDocument.Parse(handler.Body!).RootElement.TryGetProperty("api_key", out _));
        Assert.False(handler.Headers!.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task OpenAiUsesExactEndpointAndFixedMessages()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":"你好"}}]}""");
        using var http = new HttpClient(handler);
        var result = await new OpenAiCompatibleTranslationProvider(http, new Uri("https://example.com/v1/chat/completions"), "test-model", Secret).TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal("你好", result.TranslatedText);
        Assert.Equal("/v1/chat/completions", handler.Path);
        Assert.Equal("Bearer " + Secret, handler.Headers!["Authorization"]);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("test-model", body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetInt32());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("user", body.GetProperty("messages")[1].GetProperty("role").GetString());
        Assert.DoesNotContain(Secret, handler.Body);
        Assert.False(body.TryGetProperty("prompt", out _));
    }

    [Theory]
    [InlineData(401, TranslationFailureKind.Authentication)]
    [InlineData(403, TranslationFailureKind.Authentication)]
    [InlineData(429, TranslationFailureKind.Quota)]
    [InlineData(500, TranslationFailureKind.ProviderError)]
    public async Task AllProvidersMapHttpFailuresWithoutLeakingSecrets(int status, TranslationFailureKind expected)
    {
        foreach (var provider in CreateProviders(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Secret + Input) }))
        {
            var result = await provider.TranslateAsync(Auto, CancellationToken.None);
            Assert.Equal(expected, result.FailureKind);
            Assert.DoesNotContain(Secret, result.ToString());
            Assert.DoesNotContain(Input, result.ToString());
        }
    }

    [Fact]
    public async Task AllProvidersRejectMalformedPayload()
    {
        foreach (var provider in CreateProviders(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{") }))
            Assert.Equal(TranslationFailureKind.ProviderError, (await provider.TranslateAsync(Auto, CancellationToken.None)).FailureKind);
    }

    [Fact]
    public async Task AllProvidersMapNetworkFailure()
    {
        foreach (var provider in CreateProviders(_ => throw new HttpRequestException(Secret + Input)))
        {
            var result = await provider.TranslateAsync(Auto, CancellationToken.None);
            Assert.Equal(TranslationFailureKind.Network, result.FailureKind);
            Assert.DoesNotContain(Secret, result.ToString());
            Assert.DoesNotContain(Input, result.ToString());
        }
    }

    [Fact]
    public async Task OpenAiRejectsReasoningOnlyResponse()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"reasoning_content":"thought","content":""}}]}""");
        using var http = new HttpClient(handler);
        var result = await new OpenAiCompatibleTranslationProvider(http, new Uri("https://example.com/v1/chat/completions"), "test", Secret).TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal(TranslationFailureKind.ProviderError, result.FailureKind);
    }

    [Theory]
    [InlineData("<think>Need to translate this.</think>", null)]
    [InlineData(" <THINK>Need to translate this.</THINK> \n", null)]
    [InlineData("<think>Need to translate this.</think>你好", "你好")]
    [InlineData("<think>first</think> <think>second</think>", null)]
    [InlineData("你好<think>reasoning</think>", "你好")]
    [InlineData("<think>Need to translate this.", null)]
    [InlineData(" <THINK>Need to translate this.\nMore thought.", null)]
    [InlineData("<think>Need to translate this.\n\n你好", "你好")]
    public async Task OpenAiSeparatesReasoningBlocksFromTranslation(string content, string? expected)
    {
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });
        var handler = new RecordingHandler(payload);
        using var http = new HttpClient(handler);
        var result = await new OpenAiCompatibleTranslationProvider(http, new Uri("https://example.com/v1/chat/completions"), "test", Secret)
            .TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal(expected is null ? TranslationFailureKind.ProviderError : TranslationFailureKind.None, result.FailureKind);
        Assert.Equal(expected, result.TranslatedText);
    }

    [Fact]
    public async Task AzureMalformedArrayItemBecomesProviderFailure()
    {
        var handler = new RecordingHandler("[1]");
        using var http = new HttpClient(handler);
        var result = await new AzureTranslatorProvider(http, new Uri("https://example.com"), Secret).TranslateAsync(Auto, CancellationToken.None);
        Assert.Equal(TranslationFailureKind.ProviderError, result.FailureKind);
    }
    [Fact]
    public async Task AllProvidersMapTimeoutAndCancellation()
    {
        foreach (var provider in CreateProviders(_ => throw new TaskCanceledException(Secret + Input)))
            Assert.Equal(TranslationFailureKind.Timeout, (await provider.TranslateAsync(Auto, CancellationToken.None)).FailureKind);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        foreach (var provider in CreateProviders(_ => throw new TaskCanceledException(Secret + Input)))
            Assert.Equal(TranslationFailureKind.Cancelled, (await provider.TranslateAsync(Auto, cts.Token)).FailureKind);
    }

    [Fact]
    public async Task ExplicitSourceLanguagesAreSentToAzureAndDeepL()
    {
        var azure = new RecordingHandler("""[{"translations":[{"text":"ok"}]}]""");
        using var azureHttp = new HttpClient(azure);
        await new AzureTranslatorProvider(azureHttp, new Uri("https://example.com"), Secret).TranslateAsync(
            new TranslationRequest(Input, "zh-Hant", "en", TranslationPurpose.Interactive), CancellationToken.None);
        Assert.Contains("from=zh-Hant", azure.Path);
        Assert.Contains("to=en", azure.Path);
        var deepL = new RecordingHandler("""{"translations":[{"text":"ok"}]}""");
        using var deepLHttp = new HttpClient(deepL);
        await new DeepLTranslationProvider(deepLHttp, new Uri("https://example.com"), Secret).TranslateAsync(
            new TranslationRequest(Input, "en", "zh-Hant", TranslationPurpose.Interactive), CancellationToken.None);
        var body = JsonDocument.Parse(deepL.Body!).RootElement;
        Assert.Equal("EN", body.GetProperty("source_lang").GetString());
        Assert.Equal("ZH-HANT", body.GetProperty("target_lang").GetString());
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    public async Task DeepLMapsExplicitChineseSourceToZh(string source)
    {
        var handler = new RecordingHandler("""{"translations":[{"text":"Hello"}]}""");
        using var http = new HttpClient(handler);
        var result = await new DeepLTranslationProvider(http, new Uri("https://example.com"), Secret).TranslateAsync(
            new TranslationRequest(Input, source, "en", TranslationPurpose.Interactive), CancellationToken.None);
        Assert.True(result.Success);
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("ZH", body.GetProperty("source_lang").GetString());
        Assert.Equal("EN", body.GetProperty("target_lang").GetString());
    }

    [Fact]
    public async Task InvalidLanguagesAndEmptyTextAreRejectedBeforeSending()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("request should not be sent"));
        using var http = new HttpClient(handler);
        var provider = new LibreTranslateProvider(http, new Uri("https://example.com"), null);
        Assert.Equal(TranslationFailureKind.UnsupportedLanguage, (await provider.TranslateAsync(
            new TranslationRequest(Input, "xx", "en", TranslationPurpose.Interactive), CancellationToken.None)).FailureKind);
        Assert.Equal(TranslationFailureKind.InvalidRequest, (await provider.TranslateAsync(
            new TranslationRequest(" ", "en", "fr", TranslationPurpose.Interactive), CancellationToken.None)).FailureKind);
    }
    [Fact]
    public void FactoryCreatesAllProvidersAndRejectsMissingConfiguration()
    {
        using var factory = new TranslationProviderFactory(new HttpClient(new RecordingHandler("{}")));
        foreach (var kind in Enum.GetValues<TranslationProviderKind>())
        {
            var settings = new TranslationSettings(kind, "auto", "zh-Hans", kind is TranslationProviderKind.OpenAiCompatible or TranslationProviderKind.LibreTranslate ? "https://example.com/v1/chat/completions" : "", "test", "eastasia");
            Assert.NotNull(factory.Create(settings, Secret));
        }
        Assert.Throws<TranslationProviderConfigurationException>(() => factory.Create(new TranslationSettings(TranslationProviderKind.OpenAiCompatible, "auto", "zh-Hans", "https://example.com/v1/chat/completions", "", ""), Secret));
        Assert.Throws<TranslationProviderConfigurationException>(() => factory.Create(new TranslationSettings(TranslationProviderKind.DeepL, "auto", "zh-Hans", "", "", ""), null));
    }

    private static IEnumerable<ITranslationProvider> CreateProviders(Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        var uri = new Uri("https://example.com/v1/chat/completions");
        yield return new AzureTranslatorProvider(new HttpClient(new RecordingHandler(reply)), uri, Secret);
        yield return new DeepLTranslationProvider(new HttpClient(new RecordingHandler(reply)), uri, Secret);
        yield return new LibreTranslateProvider(new HttpClient(new RecordingHandler(reply)), uri, Secret);
        yield return new OpenAiCompatibleTranslationProvider(new HttpClient(new RecordingHandler(reply)), uri, "test", Secret);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;
        public RecordingHandler(string json) : this(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }) { }
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) => _reply = reply;
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string>? Headers { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToDictionary(x => x.Key, x => x.Value.Single(), StringComparer.OrdinalIgnoreCase);
            return _reply(request);
        }
    }
}
