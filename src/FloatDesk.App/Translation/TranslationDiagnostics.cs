using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

internal static class TranslationDiagnostics
{
    internal static IReadOnlyDictionary<string, object?> Build(
        TranslationProviderKind provider,
        TranslationPurpose purpose,
        int totalCharacters,
        long elapsedMilliseconds,
        TranslationFailureKind failureKind,
        int retryCount) =>
        new Dictionary<string, object?>
        {
            ["providerKind"] = provider.ToString(),
            ["purpose"] = purpose.ToString(),
            ["characterBucket"] = CharacterBucket(totalCharacters),
            ["elapsedBucket"] = ElapsedBucket(elapsedMilliseconds),
            ["failureKind"] = failureKind.ToString(),
            ["retryCount"] = Math.Max(0, retryCount)
        };

    internal static string CharacterBucket(int totalCharacters) =>
        totalCharacters switch
        {
            < 100 => "0-99",
            < 500 => "100-499",
            < 2000 => "500-1999",
            _ => "2000+"
        };

    private static string ElapsedBucket(long elapsedMilliseconds) =>
        elapsedMilliseconds switch
        {
            < 100 => "0-99ms",
            < 500 => "100-499ms",
            < 2000 => "500-1999ms",
            _ => "2000+ms"
        };
}
