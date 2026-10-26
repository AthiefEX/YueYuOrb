namespace FloatDesk.Core.Translation;

public static class TranslationLanguages
{
    public const string Auto = "auto";
    public const string SimplifiedChinese = "zh-Hans";
    public const string TraditionalChinese = "zh-Hant";
    public const string English = "en";
    public const string Japanese = "ja";
    public const string Korean = "ko";
    public const string French = "fr";
    public const string German = "de";
    public const string Spanish = "es";
    public const string Portuguese = "pt";
    public const string Russian = "ru";
    public const string Arabic = "ar";
    public const string Hebrew = "he";
    public const string Thai = "th";
    public const string Hindi = "hi";

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        Auto, SimplifiedChinese, TraditionalChinese, English, Japanese, Korean,
        French, German, Spanish, Portuguese, Russian, Arabic, Hebrew, Thai, Hindi
    };

    public static bool IsKnown(string? languageTag) =>
        languageTag is not null && Known.Contains(languageTag);

    public static bool IsTranslationLanguage(string? languageTag) =>
        IsKnown(languageTag) && !languageTag!.Equals(Auto, StringComparison.OrdinalIgnoreCase);
}
