using FloatDesk.Core.Translation;

namespace FloatDesk.Infrastructure.Translation;

public static class TranslationLanguageMapper
{
    public static string ToMyMemory(string languageTag)
    {
        if (!TranslationLanguages.IsTranslationLanguage(languageTag))
            throw new ArgumentException("Unsupported translation language.", nameof(languageTag));

        return languageTag.ToLowerInvariant() switch
        {
            "zh-hans" => "zh-CN",
            "zh-hant" => "zh-TW",
            var tag => tag
        };
    }
}
