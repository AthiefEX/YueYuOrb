using FloatDesk.Core.Models;

namespace FloatDesk.Core.Translation;

public sealed record OcrLanguageOption(string LanguageTag, string DisplayName);
public sealed record ScreenOcrResult(string? Text, string? ErrorCode, string? Message);

public interface IScreenOcrService
{
    IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; }
    Task<ScreenOcrResult> RecognizeAsync(CaptureFrame frame, string? languageTag, CancellationToken cancellationToken);
}
