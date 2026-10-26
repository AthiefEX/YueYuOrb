using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.ViewModels;

internal sealed class SettingsReloadCoordinator
{
    private readonly Func<AppSettings> _getCurrentSettings;
    private readonly ITranslationCredentialStore _credentials;
    private readonly Func<IReadOnlyList<OcrLanguageOption>> _getOcrLanguages;
    private readonly Action<SettingsViewModel> _apply;

    public SettingsReloadCoordinator(
        Func<AppSettings> getCurrentSettings,
        ITranslationCredentialStore credentials,
        Func<IReadOnlyList<OcrLanguageOption>> getOcrLanguages,
        Action<SettingsViewModel> apply)
    {
        _getCurrentSettings = getCurrentSettings ??
            throw new ArgumentNullException(nameof(getCurrentSettings));
        _credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        _getOcrLanguages = getOcrLanguages ??
            throw new ArgumentNullException(nameof(getOcrLanguages));
        _apply = apply ??
            throw new ArgumentNullException(nameof(apply));
    }

    public async Task<SettingsViewModel> ReloadAsync(
        AppSettings? savedSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var viewModel = await SettingsReloadFactory.CreateAsync(
            savedSnapshot ?? _getCurrentSettings(),
            _credentials,
            _getOcrLanguages);
        cancellationToken.ThrowIfCancellationRequested();
        _apply(viewModel);
        return viewModel;
    }
}
