using FloatDesk.Core.Geometry;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class OcrTranslationController
{
    private readonly IScreenOcrService _ocr;
    private readonly ITranslationRegionCapture _capture;
    private readonly ITranslationRouteHost _host;
    private readonly Func<string?> _ocrLanguage;

    public OcrTranslationController(
        IScreenOcrService ocr,
        ITranslationRegionCapture capture,
        ITranslationRouteHost host,
        Func<string?>? ocrLanguage = null)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _capture = capture ??
            throw new ArgumentNullException(nameof(capture));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ocrLanguage = ocrLanguage ?? (static () => null);
    }

    public async Task RunAsync(
        PixelPoint fallbackAnchor,
        CancellationToken cancellationToken)
    {
        var operation = _host.ShowInteractiveStatus(
            fallbackAnchor,
            "正在选择区域");
        try
        {
            var captured = await _capture
                .CaptureSelectedAsync(cancellationToken)
                .ConfigureAwait(false);
            if (captured is null)
            {
                _host.DismissOperation(operation);
                return;
            }

            using var frame = captured.Frame;
            var resultAnchor = new PixelPoint(
                captured.Bounds.Left,
                checked(captured.Bounds.Bottom + 8));
            var capturedOperation = operation with
            {
                Anchor = resultAnchor
            };
            _host.ShowStatus(capturedOperation, "正在识别文字…");

            ScreenOcrResult ocrResult;
            try
            {
                ocrResult = await _ocr.RecognizeAsync(
                    frame,
                    _ocrLanguage(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _host.DismissOperation(operation);
                return;
            }
            catch
            {
                _host.ShowError(
                    capturedOperation,
                    "无法识别屏幕文字，请稍后重试。");
                return;
            }

            if (ocrResult.ErrorCode is { Length: > 0 } errorCode)
            {
                _host.ShowError(
                    capturedOperation,
                    ResolveOcrFailure(errorCode));
                return;
            }

            var recognizedText = TranslationTextProcessor.Normalize(
                ocrResult.Text ?? "");
            if (recognizedText.Length == 0)
            {
                _host.ShowError(
                    capturedOperation,
                    ResolveOcrFailure("OcrNoText"));
                return;
            }

            _host.ShowStatus(capturedOperation, "正在翻译…");
            var response = await _host.TranslateAsync(
                recognizedText,
                TranslationPurpose.Interactive,
                cancellationToken).ConfigureAwait(false);
            if (response.FailureKind == TranslationFailureKind.Cancelled)
            {
                _host.DismissOperation(operation);
                return;
            }

            _host.ShowResult(capturedOperation, response);
        }
        catch (OperationCanceledException)
        {
            _host.DismissOperation(operation);
        }
        catch
        {
            _host.ShowError(
                operation,
                "无法截取所选区域，请稍后重试。");
        }
    }

    private static string ResolveOcrFailure(string errorCode) =>
        errorCode switch
        {
            "OcrLanguageUnavailable" =>
                "未安装可用的 OCR 语言包，请先在 Windows 设置中安装对应语言。",
            "OcrNoText" =>
                "未识别到文字，请重新选择更清晰的区域。",
            "OcrFailed" =>
                "无法识别屏幕文字，请稍后重试。",
            _ =>
                "无法识别屏幕文字，请稍后重试。"
        };
}
