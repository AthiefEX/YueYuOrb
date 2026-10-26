namespace FloatDesk.Core.Models;

public enum TranslationProviderKind
{
    MyMemory,
    AzureTranslator,
    DeepL,
    LibreTranslate,
    OpenAiCompatible
}

public sealed record TranslationCaptureRegion(
    string MonitorId,
    double RelativeX,
    double RelativeY,
    double RelativeWidth,
    double RelativeHeight);

public sealed record TranslationOverlayPlacement(
    string MonitorId,
    double RelativeX,
    double RelativeY);

public sealed record TranslationSettings(
    TranslationProviderKind Provider,
    string SourceLanguage,
    string TargetLanguage,
    string Endpoint,
    string Model,
    string AzureRegion)
{
    public int SubtitleRefreshIntervalMs { get; init; } = 450;
    public double SubtitleOverlayOpacity { get; init; } = 0.92;
    public double SubtitleFontSizeDip { get; init; } = 20;
    public string? OcrLanguage { get; init; }
    public TranslationCaptureRegion? SubtitleRegion { get; init; }
    public TranslationOverlayPlacement? SubtitleOverlayPlacement { get; init; }

    public static TranslationSettings CreateDefault() =>
        new(TranslationProviderKind.MyMemory, "auto", "zh-Hans", "", "", "");
}
