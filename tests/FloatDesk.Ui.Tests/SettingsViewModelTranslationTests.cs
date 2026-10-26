using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsViewModelTranslationTests
{
    [Fact]
    public void LanguageListsUseThePublishedOrder()
    {
        string[] source =
        [
            TranslationLanguages.Auto,
            TranslationLanguages.SimplifiedChinese,
            TranslationLanguages.TraditionalChinese,
            TranslationLanguages.English,
            TranslationLanguages.Japanese,
            TranslationLanguages.Korean,
            TranslationLanguages.French,
            TranslationLanguages.German,
            TranslationLanguages.Spanish,
            TranslationLanguages.Russian,
            TranslationLanguages.Arabic,
            TranslationLanguages.Portuguese,
            "it"
        ];

        Assert.Equal(source, SettingsViewModel.TranslationSourceLanguages.ToArray());
        Assert.Equal(source[1..], SettingsViewModel.TranslationTargetLanguages.ToArray());
    }

    [Fact]
    public void FromMapsCanonicalTagsToIndexesAndIndexesBackToCanonicalTags()
    {
        var original = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                SourceLanguage = "JA",
                TargetLanguage = "ZH-HANT"
            }
        };
        var viewModel = SettingsViewModel.From(original);

        Assert.Equal(
            SettingsViewModel.TranslationSourceLanguages.ToList()
                .IndexOf(TranslationLanguages.Japanese),
            viewModel.TranslationSourceLanguageIndex);
        Assert.Equal(
            SettingsViewModel.TranslationTargetLanguages.ToList()
                .IndexOf(TranslationLanguages.TraditionalChinese),
            viewModel.TranslationTargetLanguageIndex);

        viewModel.TranslationSourceLanguageIndex =
            SettingsViewModel.TranslationSourceLanguages.ToList()
                .IndexOf(TranslationLanguages.French);
        viewModel.TranslationTargetLanguageIndex =
            SettingsViewModel.TranslationTargetLanguages.ToList()
                .IndexOf(TranslationLanguages.Korean);

        var candidate = viewModel.BuildCandidate(original);

        Assert.Equal(TranslationLanguages.French, candidate.Translation.SourceLanguage);
        Assert.Equal(TranslationLanguages.Korean, candidate.Translation.TargetLanguage);
    }

    [Fact]
    public void FromSeedsOcrSelectionWithTheFirstProfileLanguage()
    {
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)"),
            new("fr-FR", "French (France)")
        ];

        var viewModel = SettingsViewModel.From(
            AppSettings.CreateDefault(),
            ocrLanguages: options);

        Assert.Equal(options, viewModel.OcrLanguages);
        Assert.Equal(0, viewModel.TranslationOcrLanguageIndex);
    }

    [Fact]
    public void OcrSelectionMergesAgainstTheSeededProfileLanguage()
    {
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)"),
            new("fr-FR", "French (France)")
        ];
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original, ocrLanguages: options);
        var concurrent = original with
        {
            Translation = original.Translation with { OcrLanguage = "fr-FR" }
        };

        Assert.Equal(
            "fr-FR",
            viewModel.BuildCandidate(concurrent).Translation.OcrLanguage);

        viewModel.TranslationOcrLanguageIndex = 1;
        var edited = viewModel.BuildCandidate(original);

        Assert.Equal("fr-FR", edited.Translation.OcrLanguage);
    }

    [Fact]
    public void SavedOcrLanguageSelectsMatchingSuppliedOption()
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
                OcrLanguage = "fr-FR"
            }
        };

        var viewModel = SettingsViewModel.From(settings, ocrLanguages: options);

        Assert.Equal(1, viewModel.TranslationOcrLanguageIndex);
    }

    [Fact]
    public void UntouchedTranslationPreservesEveryConcurrentValue()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        var latestTranslation = new TranslationSettings(
            TranslationProviderKind.AzureTranslator,
            TranslationLanguages.Japanese,
            TranslationLanguages.English,
            "https://translator.example.test",
            "concurrent-model",
            "westeurope")
        {
            SubtitleRefreshIntervalMs = 900,
            SubtitleOverlayOpacity = 0.75,
            SubtitleFontSizeDip = 30,
            OcrLanguage = "fr-FR"
        };
        var latest = original with { Translation = latestTranslation };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(latestTranslation, candidate.Translation);
    }

    [Fact]
    public void EditedTranslationFieldsWinOverEveryConcurrentValue()
    {
        var original = AppSettings.CreateDefault();
        OcrLanguageOption[] options =
        [
            new("en-US", "English (United States)"),
            new("fr-FR", "French (France)")
        ];
        var viewModel = SettingsViewModel.From(original, ocrLanguages: options);
        viewModel.TranslationProviderIndex =
            SettingsViewModel.TranslationProviders.ToList()
                .IndexOf(TranslationProviderKind.OpenAiCompatible);
        viewModel.TranslationSourceLanguageIndex =
            SettingsViewModel.TranslationSourceLanguages.ToList()
                .IndexOf(TranslationLanguages.German);
        viewModel.TranslationTargetLanguageIndex =
            SettingsViewModel.TranslationTargetLanguages.ToList()
                .IndexOf(TranslationLanguages.Spanish);
        viewModel.TranslationEndpoint = "https://edited.example.test/v1/chat/completions";
        viewModel.TranslationModel = "edited-model";
        viewModel.TranslationAzureRegion = "eastasia";
        viewModel.TranslationOcrLanguageIndex = 1;
        viewModel.SubtitleRefreshIntervalMs = 600;
        viewModel.SubtitleOverlayOpacityPercent = 88;
        viewModel.SubtitleFontSizeDip = 26;
        var concurrentTranslation = new TranslationSettings(
            TranslationProviderKind.DeepL,
            TranslationLanguages.Japanese,
            TranslationLanguages.English,
            "https://concurrent.example.test",
            "concurrent-model",
            "westeurope")
        {
            SubtitleRefreshIntervalMs = 1200,
            SubtitleOverlayOpacity = 0.65,
            SubtitleFontSizeDip = 16,
            OcrLanguage = "en-US"
        };
        var latest = original with { Translation = concurrentTranslation };

        var candidate = viewModel.BuildCandidate(latest);

        Assert.Equal(TranslationProviderKind.OpenAiCompatible, candidate.Translation.Provider);
        Assert.Equal(TranslationLanguages.German, candidate.Translation.SourceLanguage);
        Assert.Equal(TranslationLanguages.Spanish, candidate.Translation.TargetLanguage);
        Assert.Equal("https://edited.example.test/v1/chat/completions", candidate.Translation.Endpoint);
        Assert.Equal("edited-model", candidate.Translation.Model);
        Assert.Equal("eastasia", candidate.Translation.AzureRegion);
        Assert.Equal("fr-FR", candidate.Translation.OcrLanguage);
        Assert.Equal(600, candidate.Translation.SubtitleRefreshIntervalMs);
        Assert.Equal(0.88, candidate.Translation.SubtitleOverlayOpacity, 10);
        Assert.Equal(26, candidate.Translation.SubtitleFontSizeDip);
    }

    [Theory]
    [InlineData("selection", "翻译选中")]
    [InlineData("region", "框选翻译")]
    [InlineData("subtitles", "字幕翻译")]
    public void InvalidTranslationHotkeyPreventsApply(
        string property,
        string expectedErrorText)
    {
        var viewModel = SettingsViewModel.From(AppSettings.CreateDefault());
        switch (property)
        {
            case "selection":
                viewModel.TranslationSelectionHotkey = "Ctrl+?";
                break;
            case "region":
                viewModel.TranslationRegionHotkey = "Ctrl+?";
                break;
            case "subtitles":
                viewModel.SubtitlesHotkey = "Ctrl+?";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property));
        }

        var valid = viewModel.TryBuildCandidate(
            AppSettings.CreateDefault(),
            out _,
            out var error);

        Assert.False(valid);
        Assert.Contains(expectedErrorText, error, StringComparison.Ordinal);
        Assert.Contains("快捷键", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingSettingsDoNotGainDefaultTranslationWheelActions()
    {
        var original = AppSettings.CreateDefault() with
        {
            WheelActions = [WheelActionKind.Notes, WheelActionKind.Settings]
        };
        var viewModel = SettingsViewModel.From(original);

        Assert.False(viewModel.ShowTranslateSelectionInWheel);
        Assert.False(viewModel.ShowTranslateRegionInWheel);
        Assert.False(viewModel.ShowTranslateSubtitlesInWheel);

        var unchanged = viewModel.BuildCandidate(original);

        Assert.Equal(original.WheelActions, unchanged.WheelActions);

        viewModel.ShowTranslateRegionInWheel = true;
        var edited = viewModel.BuildCandidate(original);

        Assert.Contains(WheelActionKind.TranslateRegion, edited.WheelActions);
        Assert.DoesNotContain(WheelActionKind.TranslateSelection, edited.WheelActions);
        Assert.DoesNotContain(WheelActionKind.TranslateSubtitles, edited.WheelActions);
    }

    [Fact]
    public void AllTranslationWheelTogglesCanBeAddedAndRemoved()
    {
        var original = AppSettings.CreateDefault();
        var viewModel = SettingsViewModel.From(original);
        viewModel.ShowTranslateSelectionInWheel = false;
        viewModel.ShowTranslateRegionInWheel = false;
        viewModel.ShowTranslateSubtitlesInWheel = false;

        var removed = viewModel.BuildCandidate(original);

        Assert.DoesNotContain(WheelActionKind.TranslateSelection, removed.WheelActions);
        Assert.DoesNotContain(WheelActionKind.TranslateRegion, removed.WheelActions);
        Assert.DoesNotContain(WheelActionKind.TranslateSubtitles, removed.WheelActions);

        var restored = SettingsViewModel.From(removed);
        restored.ShowTranslateSelectionInWheel = true;
        restored.ShowTranslateRegionInWheel = true;
        restored.ShowTranslateSubtitlesInWheel = true;

        var added = restored.BuildCandidate(removed);

        Assert.Contains(WheelActionKind.TranslateSelection, added.WheelActions);
        Assert.Contains(WheelActionKind.TranslateRegion, added.WheelActions);
        Assert.Contains(WheelActionKind.TranslateSubtitles, added.WheelActions);
    }

    [Fact]
    public void SavedKeyStateAndClearFlagAreExposedWithoutAnApiKeyProperty()
    {
        var viewModel = SettingsViewModel.From(
            AppSettings.CreateDefault(),
            hasSavedTranslationKey: true);

        Assert.True(viewModel.HasSavedTranslationKey);
        Assert.False(viewModel.ClearSavedTranslationKey);

        viewModel.ClearSavedTranslationKey = true;

        Assert.True(viewModel.ClearSavedTranslationKey);
        Assert.DoesNotContain(
            typeof(SettingsViewModel).GetProperties(),
            property => property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase));
    }
}
