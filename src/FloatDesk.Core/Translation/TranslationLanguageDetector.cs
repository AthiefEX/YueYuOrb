using System.Text.RegularExpressions;

namespace FloatDesk.Core.Translation;

public sealed record TranslationLanguageDetection(string LanguageTag, double Confidence);

public static class TranslationLanguageDetector
{
    private static readonly (string Tag, string[] Words)[] LatinWords =
    [
        (TranslationLanguages.French, ["bonjour", "le", "la", "les", "et", "des", "est"]),
        (TranslationLanguages.German, ["hallo", "die", "der", "das", "und", "ist", "nicht"]),
        (TranslationLanguages.Spanish, ["hola", "el", "la", "los", "las", "y", "es"]),
        (TranslationLanguages.Portuguese, ["olá", "o", "a", "os", "as", "e", "não"]),
        (TranslationLanguages.English, ["hello", "the", "and", "is", "are", "with", "this"])
    ];

    private const string TraditionalSignals = "體測譯語國學說這個們會對時書車門開關實後發";

    public static TranslationLanguageDetection Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Any(c => c is >= '\u3040' and <= '\u30ff')) return new(TranslationLanguages.Japanese, 0.99);
        if (text.Any(c => c is >= '\uac00' and <= '\ud7af' or >= '\u1100' and <= '\u11ff')) return new(TranslationLanguages.Korean, 0.99);
        if (text.Any(c => c is >= '\u0400' and <= '\u052f')) return new(TranslationLanguages.Russian, 0.98);
        if (text.Any(c => c is >= '\u0600' and <= '\u06ff')) return new(TranslationLanguages.Arabic, 0.98);
        if (text.Any(c => c is >= '\u0590' and <= '\u05ff')) return new(TranslationLanguages.Hebrew, 0.98);
        if (text.Any(c => c is >= '\u0e00' and <= '\u0e7f')) return new(TranslationLanguages.Thai, 0.98);
        if (text.Any(c => c is >= '\u0900' and <= '\u097f')) return new(TranslationLanguages.Hindi, 0.98);
        if (text.Any(c => c is >= '\u3400' and <= '\u9fff'))
        {
            var traditional = text.Count(c => TraditionalSignals.Contains(c));
            return new(traditional >= 2 ? TranslationLanguages.TraditionalChinese : TranslationLanguages.SimplifiedChinese,
                traditional >= 2 ? 0.90 : 0.80);
        }

        var words = Regex.Matches(text.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToHashSet();
        var best = LatinWords.Select(entry => (entry.Tag, Score: entry.Words.Count(words.Contains)))
            .OrderByDescending(entry => entry.Score).First();
        return best.Score == 0
            ? new(TranslationLanguages.English, 0.20)
            : new(best.Tag, Math.Min(0.95, 0.55 + best.Score * 0.10));
    }
}
