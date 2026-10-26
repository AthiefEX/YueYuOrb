using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.ViewModels;

internal static class SettingsReloadFactory
{
    public static async Task<SettingsViewModel> CreateAsync(
        AppSettings settings,
        ITranslationCredentialStore credentials,
        Func<IReadOnlyList<OcrLanguageOption>> getOcrLanguages)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(getOcrLanguages);

        var hasSavedKey = await HasSavedTranslationKeyAsync(
            settings.Translation,
            credentials);
        return SettingsViewModel.From(
            settings,
            hasSavedKey,
            getOcrLanguages());
    }

    private static async Task<bool> HasSavedTranslationKeyAsync(
        TranslationSettings settings,
        ITranslationCredentialStore credentials)
    {
        if (settings.Provider == TranslationProviderKind.MyMemory)
            return false;
        try
        {
            return await credentials.HasAsync(
                settings.Provider,
                settings.Endpoint,
                CancellationToken.None);
        }
        catch
        {
            return false;
        }
    }
}
