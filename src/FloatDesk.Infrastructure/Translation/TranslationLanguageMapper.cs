using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public static class TranslationLanguageMapper
{
    private static string Validate(string languageTag)
    {
        if (!TranslationLanguages.IsTranslationLanguage(languageTag))
            throw new ArgumentException("Unsupported translation language.", nameof(languageTag));
        return languageTag.ToLowerInvariant();
    }

    public static string ToMyMemory(string languageTag) => Validate(languageTag) switch
    {
        "zh-hans" => "zh-CN",
        "zh-hant" => "zh-TW",
        var tag => tag
    };

    public static string ToAzure(string languageTag) => Validate(languageTag) switch
    {
        "zh-hans" => "zh-Hans",
        "zh-hant" => "zh-Hant",
        var tag => tag
    };

    public static string ToDeepL(string languageTag) => Validate(languageTag) switch
    {
        "zh-hans" => "ZH-HANS",
        "zh-hant" => "ZH-HANT",
        var tag => tag.ToUpperInvariant()
    };

    public static string ToDeepLSource(string languageTag) => Validate(languageTag) switch
    {
        "zh-hans" or "zh-hant" => "ZH",
        var tag => tag.ToUpperInvariant()
    };

    public static string ToLibre(string languageTag) => Validate(languageTag) switch
    {
        "zh-hans" or "zh-hant" => "zh",
        var tag => tag
    };
}
