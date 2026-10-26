using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class TranslationSettingsValidationTests
{
    [Fact]
    public void DefaultTranslationSettingsUseFreeProviderAndChineseTarget()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(TranslationProviderKind.MyMemory, settings.Translation.Provider);
        Assert.Equal("auto", settings.Translation.SourceLanguage);
        Assert.Equal("zh-Hans", settings.Translation.TargetLanguage);
        Assert.Equal(450, settings.Translation.SubtitleRefreshIntervalMs);
        Assert.Equal(0.92, settings.Translation.SubtitleOverlayOpacity);
        Assert.Equal(20, settings.Translation.SubtitleFontSizeDip);
        Assert.Null(settings.Translation.OcrLanguage);
        Assert.Empty(SettingsValidator.Validate(settings));
    }

    [Fact]
    public void DefaultsIncludeTranslationHotkeysAndWheelActions()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Equal(new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x54), settings.Hotkeys[HotkeyAction.TranslateSelection]);
        Assert.Equal(new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4F), settings.Hotkeys[HotkeyAction.TranslateRegion]);
        Assert.Equal(new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x4C), settings.Hotkeys[HotkeyAction.ToggleSubtitles]);
        Assert.Contains(WheelActionKind.TranslateSelection, settings.WheelActions);
        Assert.Contains(WheelActionKind.TranslateRegion, settings.WheelActions);
        Assert.Contains(WheelActionKind.TranslateSubtitles, settings.WheelActions);
    }

    [Theory]
    [InlineData("http://example.com/v1/chat/completions")]
    [InlineData("https://user:pass@example.com/api")]
    [InlineData("https://example.com/api#secret")]
    [InlineData("https://example.com/api?api_key=secret")]
    [InlineData("relative/path")]
    public void ValidatorRejectsInsecureOrMalformedCustomEndpoint(string endpoint)
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.OpenAiCompatible,
                Endpoint = endpoint,
                Model = "test"
            }
        };
        Assert.Contains(SettingsValidator.Validate(settings), error => error.Contains("Endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void EndpointPolicyNormalizesValidHttpsAndRejectsInsecureUrl()
    {
        Assert.True(TranslationEndpointPolicy.TryNormalizeHttps(" https://example.com/api ", out var normalized, out var error));
        Assert.Equal("https://example.com/api", normalized);
        Assert.Null(error);
        Assert.False(TranslationEndpointPolicy.TryNormalizeHttps("http://example.com", out _, out error));
        Assert.Contains("HTTPS", error);
    }

    [Fact]
    public void ValidatorRejectsUnknownLanguagesAndInvalidProvider()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = (TranslationProviderKind)99,
                SourceLanguage = "xx-Unknown",
                TargetLanguage = "auto"
            }
        };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, error => error.Contains("Provider", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SourceLanguage", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("TargetLanguage", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAcceptsItalianAsATranslationLanguage()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                SourceLanguage = TranslationLanguages.Italian,
                TargetLanguage = TranslationLanguages.Italian
            }
        };

        Assert.Empty(SettingsValidator.Validate(settings));
    }

    [Fact]
    public void ValidatorReportsMissingTargetLanguageWithoutThrowing()
    {
        var original = AppSettings.CreateDefault();
        var settings = original with
        {
            Translation = original.Translation with { TargetLanguage = null! }
        };

        Assert.Contains(SettingsValidator.Validate(settings),
            error => error.Contains("TargetLanguage", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsOutOfRangeSubtitleValuesAndCoordinates()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                SubtitleRefreshIntervalMs = 299,
                SubtitleOverlayOpacity = double.NaN,
                SubtitleFontSizeDip = 37,
                SubtitleRegion = new TranslationCaptureRegion("monitor", 0.8, 0.2, 0.3, 0.5),
                SubtitleOverlayPlacement = new TranslationOverlayPlacement("monitor", -0.1, 0.5)
            }
        };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, error => error.Contains("SubtitleRefreshIntervalMs", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SubtitleOverlayOpacity", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SubtitleFontSizeDip", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SubtitleRegion", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("SubtitleOverlayPlacement", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRequiresCustomProviderEndpointAndOpenAiModel()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with { Provider = TranslationProviderKind.OpenAiCompatible }
        };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, error => error.Contains("Endpoint", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Model", StringComparison.Ordinal));
    }
}
