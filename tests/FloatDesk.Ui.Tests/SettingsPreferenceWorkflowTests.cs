using FloatDesk.App.ViewModels;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsPreferenceWorkflowTests
{
    [Fact]
    public async Task AsyncStartupRegistrationIsAwaitedBeforeApplyingPreferences()
    {
        var settings = new RecordingSettingsCoordinator(AppSettings.CreateDefault());
        var reload = new SettingsReloadCoordinator(() => settings.Current, new FakeCredentialStore(hasSavedKey: false), () => [], _ => { });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var workflow = new SettingsPreferenceWorkflow(settings, reload, _ => throw new InvalidOperationException("Must use async path"), _ => applied = true, _ => gate.Task);
        var saving = workflow.ResetAsync(AppSettings.CreateDefault(), CancellationToken.None);
        Assert.False(saving.IsCompleted); Assert.False(applied);
        gate.SetResult();
        Assert.True((await saving).Success); Assert.True(applied);
    }
    [Fact]
    public async Task ImportReloadsAndAppliesRealOcrAndCredentialState()
    {
        var current = AppSettings.CreateDefault() with
        {
            Windows =
            [
                new WindowPlacement(
                    "orb",
                    @"\\.\DISPLAY1",
                    0.5,
                    0.5,
                    56,
                    56)
            ]
        };
        var imported = current with
        {
            General = new GeneralSettings(true, AppTheme.Dark),
            Translation = current.Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com",
                OcrLanguage = "fr-FR"
            }
        };
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)"),
            new("fr-FR", "French (France)")
        ];
        var settings = new RecordingSettingsCoordinator(current);
        var reloaded = new List<SettingsViewModel>();
        var applied = new List<AppSettings>();
        var startup = new List<bool>();
        var reload = new SettingsReloadCoordinator(
            () => settings.Current,
            new FakeCredentialStore(hasSavedKey: true),
            () => options,
            reloaded.Add);
        var workflow = new SettingsPreferenceWorkflow(
            settings,
            reload,
            startup.Add,
            applied.Add);

        var result = await workflow.ImportAsync(
            imported,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, settings.UpdateCount);
        Assert.Equal(imported.Translation, settings.Current.Translation);
        Assert.Equal(current.Windows, settings.Current.Windows);
        Assert.Equal([true], startup);
        var viewModel = Assert.Single(reloaded);
        Assert.Equal(options, viewModel.OcrLanguages);
        Assert.Equal(1, viewModel.TranslationOcrLanguageIndex);
        Assert.True(viewModel.HasSavedTranslationKey);
        Assert.Equal([settings.Current], applied);
    }

    [Fact]
    public async Task ResetReloadsAndAppliesRealOcrAndCredentialState()
    {
        var current = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            },
            Windows =
            [
                new WindowPlacement(
                    "orb",
                    @"\\.\DISPLAY1",
                    0.25,
                    0.5,
                    56,
                    56)
            ]
        };
        var defaults = AppSettings.CreateDefault();
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)")
        ];
        var settings = new RecordingSettingsCoordinator(current);
        var reloaded = new List<SettingsViewModel>();
        var applied = new List<AppSettings>();
        var startup = new List<bool>();
        var reload = new SettingsReloadCoordinator(
            () => settings.Current,
            new FakeCredentialStore(hasSavedKey: true),
            () => options,
            reloaded.Add);
        var workflow = new SettingsPreferenceWorkflow(
            settings,
            reload,
            startup.Add,
            applied.Add);

        var result = await workflow.ResetAsync(
            defaults,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, settings.UpdateCount);
        Assert.Equal(defaults.Translation, settings.Current.Translation);
        Assert.Equal(current.Windows, settings.Current.Windows);
        Assert.Equal([false], startup);
        var viewModel = Assert.Single(reloaded);
        Assert.Equal(options, viewModel.OcrLanguages);
        Assert.False(viewModel.HasSavedTranslationKey);
        Assert.Equal([settings.Current], applied);
    }

    private sealed class RecordingSettingsCoordinator(AppSettings current)
        : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = current;
        public int UpdateCount { get; private set; }

        public event Action<AppSettings>? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken)
        {
            UpdateCount++;
            Current = update(Current);
            Changed?.Invoke(Current);
            return Task.FromResult(OperationResult.Ok());
        }
    }

    private sealed class FakeCredentialStore(bool hasSavedKey)
        : ITranslationCredentialStore
    {
        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(hasSavedKey);

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
