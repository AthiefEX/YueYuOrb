using FloatDesk.App.Translation;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class OcrTranslationControllerTests
{
    [Fact]
    public async Task ExtractOnlyReturnsRecognizedTextWithoutCallingTranslation()
    {
        var frame = SolidFrame(0x40);
        var host = new RecordingRouteHost();
        var controller = new OcrTranslationController(
            new FakeOcr(new ScreenOcrResult("  extracted text  ", null, null)),
            new FakeRegionCapture(new CapturedTranslationRegion(frame, new PixelRect(10, 20, 200, 80))),
            host);

        await controller.RunAsync(new PixelPoint(10, 20), CancellationToken.None, extractOnly: true);

        Assert.Equal("extracted text", host.LastExtractedText);
        Assert.Equal(0, host.TranslateCount);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Bgra32);
    }

    [Fact]
    public async Task RecognizedTextIsTheOnlyContentSentToTranslation()
    {
        var frame = SolidFrame(0x40);
        var ocr = new FakeOcr(
            new ScreenOcrResult("  Recognized text  ", null, null));
        var capture = new FakeRegionCapture(
            new CapturedTranslationRegion(
                frame,
                new PixelRect(10, 20, 200, 80)));
        var host = new RecordingRouteHost();
        var controller = new OcrTranslationController(
            ocr,
            capture,
            host);

        await controller.RunAsync(
            new PixelPoint(10, 20),
            CancellationToken.None);

        Assert.Same(frame, ocr.LastFrame);
        Assert.Equal("Recognized text", host.LastText);
        Assert.Equal(TranslationPurpose.Interactive, host.LastPurpose);
        Assert.True(host.LastResult!.Success);
        Assert.Equal(new PixelPoint(10, 108), host.LastResultAnchor);
        Assert.Equal(
            ["正在选择区域", "正在识别文字…", "正在翻译…"],
            host.Statuses);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Bgra32);
    }

    [Fact]
    public async Task MissingOcrLanguageShowsActionableErrorWithoutTranslating()
    {
        using var frame = SolidFrame(0x20);
        var host = new RecordingRouteHost();
        var controller = new OcrTranslationController(
            new FakeOcr(new ScreenOcrResult(
                null,
                "OcrLanguageUnavailable",
                "请安装 OCR 语言包。")),
            new FakeRegionCapture(
                new CapturedTranslationRegion(
                    frame,
                    new PixelRect(1, 2, 30, 40))),
            host);

        await controller.RunAsync(
            new PixelPoint(1, 2),
            CancellationToken.None);

        Assert.Contains("语言包", host.LastError);
        Assert.Equal(0, host.TranslateCount);
    }

    [Fact]
    public async Task EmptyOcrTextShowsNoTextErrorWithoutTranslating()
    {
        using var frame = SolidFrame(0x20);
        var host = new RecordingRouteHost();
        var controller = new OcrTranslationController(
            new FakeOcr(new ScreenOcrResult(
                " \r\n ",
                null,
                null)),
            new FakeRegionCapture(
                new CapturedTranslationRegion(
                    frame,
                    new PixelRect(1, 2, 30, 40))),
            host);

        await controller.RunAsync(
            new PixelPoint(1, 2),
            CancellationToken.None);

        Assert.Contains("未识别到文字", host.LastError);
        Assert.Equal(0, host.TranslateCount);
    }

    [Fact]
    public async Task CancelledRegionSelectionDismissesTransientOperation()
    {
        var host = new RecordingRouteHost();
        var controller = new OcrTranslationController(
            new FakeOcr(new ScreenOcrResult("unused", null, null)),
            new FakeRegionCapture(null),
            host);

        await controller.RunAsync(
            new PixelPoint(1, 2),
            CancellationToken.None);

        Assert.Equal(1, host.DismissCount);
        Assert.Equal(0, host.TranslateCount);
    }

    private static CaptureFrame SolidFrame(byte value) =>
        new(
            new PixelRect(0, 0, 4, 4),
            16,
            Enumerable.Repeat(value, 4 * 4 * 4).ToArray());

    private sealed class FakeOcr(ScreenOcrResult result) : IScreenOcrService
    {
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
        [
            new("en-US", "English")
        ];

        public CaptureFrame? LastFrame { get; private set; }

        public Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            LastFrame = frame;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeRegionCapture(
        CapturedTranslationRegion? result) : ITranslationRegionCapture
    {
        public Task<CapturedTranslationRegion?> CaptureSelectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion region,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RecordingRouteHost : ITranslationRouteHost
    {
        private int _nextOperationId;

        public List<string> Statuses { get; } = [];
        public string? LastText { get; private set; }
        public TranslationPurpose? LastPurpose { get; private set; }
        public TranslationResponse? LastResult { get; private set; }
        public string? LastError { get; private set; }
        public PixelPoint LastResultAnchor { get; private set; }
        public int TranslateCount { get; private set; }
        public int DismissCount { get; private set; }
        public string? LastExtractedText { get; private set; }

        public void ShowExtractedText(TranslationRouteHandle operation, string text) =>
            LastExtractedText = text;

        public void ApplySettings(AppSettings settings)
        {
        }

        public TranslationRouteHandle ShowStatus(
            PixelPoint anchor,
            string status)
        {
            Statuses.Add(status);
            return new TranslationRouteHandle(
                default,
                new PixelPoint(++_nextOperationId, 0));
        }

        public void ShowStatus(
            TranslationRouteHandle operation,
            string status) =>
            Statuses.Add(status);

        public void ShowResult(
            TranslationRouteHandle operation,
            TranslationResponse response)
        {
            LastResult = response;
            LastResultAnchor = operation.Anchor;
        }

        public void ShowError(
            TranslationRouteHandle operation,
            string message) =>
            LastError = message;

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken)
        {
            TranslateCount++;
            LastText = text;
            LastPurpose = purpose;
            return Task.FromResult(
                TranslationResponse.Ok(
                    text,
                    "已翻译",
                    "en",
                    "Test provider"));
        }

        public void DismissActiveOperation() => DismissCount++;
    }
}
