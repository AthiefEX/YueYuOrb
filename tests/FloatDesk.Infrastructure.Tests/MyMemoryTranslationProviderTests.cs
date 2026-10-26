using System.Net;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Infrastructure.Translation;

namespace FloatDesk.Infrastructure.Tests;

public sealed class MyMemoryTranslationProviderTests
{
    [Fact]
    public async Task SendsEncodedQueryAndMappedLangPairAndDecodesEntities()
    {
        Uri? requested = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return Json("""{"responseData":{"translatedText":"Tom &amp; Jerry &#39;ok&#39;"},"responseStatus":200}""");
        }));
        var provider = new MyMemoryTranslationProvider(http);

        var result = await provider.TranslateAsync(Request("Tom & Jerry"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Tom & Jerry 'ok'", result.TranslatedText);
        Assert.Equal("https", requested!.Scheme);
        Assert.Equal("api.mymemory.translated.net", requested.Host);
        Assert.Contains("q=Tom%20%26%20Jerry", requested.Query);
        Assert.Contains("langpair=en%7Czh-CN", requested.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("JA", "ja")]
    public void MapperUsesMyMemoryTags(string canonical, string expected) =>
        Assert.Equal(expected, TranslationLanguageMapper.ToMyMemory(canonical));

    [Fact]
    public void MapperRejectsUnknownLanguage() =>
        Assert.Throws<ArgumentException>(() => TranslationLanguageMapper.ToMyMemory("xx"));

    [Fact]
    public void ProviderMappersRepresentItalian()
    {
        Assert.Equal("it", TranslationLanguageMapper.ToMyMemory(TranslationLanguages.Italian));
        Assert.Equal("it", TranslationLanguageMapper.ToAzure(TranslationLanguages.Italian));
        Assert.Equal("IT", TranslationLanguageMapper.ToDeepL(TranslationLanguages.Italian));
        Assert.Equal("IT", TranslationLanguageMapper.ToDeepLSource(TranslationLanguages.Italian));
        Assert.Equal("it", TranslationLanguageMapper.ToLibre(TranslationLanguages.Italian));
    }

    [Fact]
    public void AdvertisesMyMemoryLimits()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));
        var limits = new MyMemoryTranslationProvider(http).Capabilities;
        Assert.False(limits.SupportsAutoDetect);
        Assert.Equal(5000, limits.MaxCharacters);
        Assert.Equal(450, limits.MaxUtf8BytesPerRequest);
        Assert.Equal(10, limits.MaxChunks);
        Assert.Equal(1, limits.MaxConcurrentRequests);
        Assert.Equal(TimeSpan.FromSeconds(1), limits.MinimumRequestInterval);
    }

    [Theory]
    [InlineData("""{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY"},"responseStatus":429}""")]
    [InlineData("""{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY"},"responseStatus":200}""")]
    [InlineData("""{"responseStatus":429,"responseDetails":"Daily quota exceeded"}""")]
    public async Task QuotaPayloadBecomesQuotaFailure(string payload)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(payload)));
        var result = await new MyMemoryTranslationProvider(http).TranslateAsync(Request("Hello"), CancellationToken.None);
        Assert.Equal(TranslationFailureKind.Quota, result.FailureKind);
    }

    [Fact]
    public async Task Http429BecomesQuotaFailure()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var result = await new MyMemoryTranslationProvider(http).TranslateAsync(Request("Hello"), CancellationToken.None);
        Assert.Equal(TranslationFailureKind.Quota, result.FailureKind);
    }

    [Theory]
    [InlineData("Your monthly quota is 10 GB")]
    [InlineData("Warning: wet floor")]
    public async Task OrdinaryTranslatedWordsAreSuccessful(string translatedText)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            responseData = new { translatedText },
            responseStatus = 200
        });
        using var http = new HttpClient(new StubHandler(_ => Json(payload)));

        var result = await new MyMemoryTranslationProvider(http).TranslateAsync(Request("Hello"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(translatedText, result.TranslatedText);
    }

    [Theory]
    [InlineData('a', 450, true)]
    [InlineData('a', 451, false)]
    [InlineData('界', 150, true)]
    [InlineData('界', 151, false)]
    public async Task EnforcesUtf8ByteLimitBeforeSending(char character, int count, bool accepted)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return Json("""{"responseData":{"translatedText":"ok"},"responseStatus":200}""");
        }));

        var result = await new MyMemoryTranslationProvider(http).TranslateAsync(Request(new string(character, count)), CancellationToken.None);

        Assert.Equal(accepted ? TranslationFailureKind.None : TranslationFailureKind.InvalidRequest, result.FailureKind);
        Assert.Equal(accepted ? 1 : 0, calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"responseData":{"translatedText":""},"responseStatus":200}""")]
    public async Task MalformedOrMissingTranslationBecomesProviderFailure(string payload)
    {
        using var http = new HttpClient(new StubHandler(_ => Json(payload)));
        var result = await new MyMemoryTranslationProvider(http).TranslateAsync(Request("Hello"), CancellationToken.None);
        Assert.Equal(TranslationFailureKind.ProviderError, result.FailureKind);
    }

    [Fact]
    public void FactoryCreatesMyMemoryAndExposesCustomDefaults()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));
        using var factory = new TranslationProviderFactory(http);
        Assert.IsType<MyMemoryTranslationProvider>(factory.Create(TranslationSettings.CreateDefault(), null));
        Assert.IsType<DeepLTranslationProvider>(factory.Create(TranslationSettings.CreateDefault() with { Provider = TranslationProviderKind.DeepL }, "key"));
        Assert.Equal("https://api.mymemory.translated.net/get", TranslationEndpointDefaults.For(TranslationProviderKind.MyMemory));
        Assert.Equal("https://api-free.deepl.com", TranslationEndpointDefaults.For(TranslationProviderKind.DeepL));
    }

    [Fact]
    public async Task FactoryDoesNotDisposeInjectedHttpClient()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("""{"responseData":{"translatedText":"Bonjour"},"responseStatus":200}""")));
        var factory = new TranslationProviderFactory(http);
        factory.Dispose();
        var response = await http.GetAsync("https://example.com/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static TranslationRequest Request(string text) =>
        new(text, "en", "zh-Hans", TranslationPurpose.Interactive);

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(value) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
