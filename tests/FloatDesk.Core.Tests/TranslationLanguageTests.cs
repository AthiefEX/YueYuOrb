using FloatDesk.Core.Translation;

namespace FloatDesk.Core.Tests;

public sealed class TranslationLanguageTests
{
    [Theory]
    [InlineData("这是中文文本。", "zh-Hans")]
    [InlineData("這是繁體中文測試。", "zh-Hant")]
    [InlineData("これは日本語です。", "ja")]
    [InlineData("안녕하세요 반갑습니다.", "ko")]
    [InlineData("Привет, как дела?", "ru")]
    [InlineData("مرحبا كيف حالك", "ar")]
    [InlineData("שלום עולם", "he")]
    [InlineData("สวัสดีครับ", "th")]
    [InlineData("नमस्ते दुनिया", "hi")]
    public void DetectsScriptedLanguages(string text, string expected)
    {
        var result = TranslationLanguageDetector.Detect(text);
        Assert.Equal(expected, result.LanguageTag);
        Assert.InRange(result.Confidence, 0, 1);
    }

    [Theory]
    [InlineData("Bonjour le monde et les amis", "fr")]
    [InlineData("Hola el mundo y los amigos", "es")]
    [InlineData("Hallo die Welt und das Leben", "de")]
    [InlineData("Olá o mundo e os amigos", "pt")]
    public void ScoresLatinStopwords(string text, string expected) =>
        Assert.Equal(expected, TranslationLanguageDetector.Detect(text).LanguageTag);

    [Fact]
    public void UnknownLatinFallsBackToEnglishWithLowConfidence()
    {
        var result = TranslationLanguageDetector.Detect("qwerty xyzzy");
        Assert.Equal("en", result.LanguageTag);
        Assert.True(result.Confidence < 0.5);
    }
}
