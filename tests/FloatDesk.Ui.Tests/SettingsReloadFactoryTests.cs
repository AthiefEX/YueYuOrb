using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsReloadFactoryTests
{
    [Fact]
    public async Task ReloadLoadsInstalledOcrOptionsAndSavedCredentialState()
    {
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)"),
            new("fr-FR", "French (France)")
        ];
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com",
                OcrLanguage = "fr-FR"
            }
        };
        var credentials = new FakeCredentialStore(hasSavedKey: true);
        var optionsCalls = 0;

        var viewModel = await SettingsReloadFactory.CreateAsync(
            settings,
            credentials,
            () =>
            {
                optionsCalls++;
                return options;
            });

        Assert.Equal(1, optionsCalls);
        Assert.Equal(options, viewModel.OcrLanguages);
        Assert.Equal(1, viewModel.TranslationOcrLanguageIndex);
        Assert.True(viewModel.HasSavedTranslationKey);
        Assert.Equal(1, credentials.HasCalls);
    }

    [Fact]
    public async Task ReloadStillRequestsRealOcrOptionsForFreeProvider()
    {
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)")
        ];
        var settings = AppSettings.CreateDefault();
        var credentials = new FakeCredentialStore(hasSavedKey: true);

        var viewModel = await SettingsReloadFactory.CreateAsync(
            settings,
            credentials,
            () => options);

        Assert.Equal(options, viewModel.OcrLanguages);
        Assert.False(viewModel.HasSavedTranslationKey);
        Assert.Equal(0, credentials.HasCalls);
    }

    private sealed class FakeCredentialStore(bool hasSavedKey)
        : ITranslationCredentialStore
    {
        public int HasCalls { get; private set; }

        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken)
        {
            HasCalls++;
            return Task.FromResult(hasSavedKey);
        }

        public Task<string?> GetAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SaveAsync(
            TranslationProviderKind provider,
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
