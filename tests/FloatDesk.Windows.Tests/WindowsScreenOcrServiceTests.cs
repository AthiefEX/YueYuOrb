using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Translation;

namespace FloatDesk.Windows.Tests;

public sealed class WindowsScreenOcrServiceTests
{
    [Fact]
    public void WindowsOcrReportsInstalledRecognizerLanguages()
    {
        using var service = new WindowsScreenOcrService();

        Assert.All(
            service.AvailableLanguages,
            option => Assert.False(string.IsNullOrWhiteSpace(option.LanguageTag)));
    }

    [Fact]
    public void AvailableLanguagesPreserveOrderDeduplicateTagsAndUseSafeNames()
    {
        var backend = new FakeOcrBackend
        {
            AvailableLanguages =
            [
                new OcrLanguageOption("zh-Hans", "简体中文"),
                new OcrLanguageOption("en-US", "English"),
                new OcrLanguageOption("zh-hans", "重复项"),
                new OcrLanguageOption("fr-FR", " ")
            ]
        };
        using var service = new WindowsScreenOcrService(backend);

        Assert.Equal(
            [
                new OcrLanguageOption("zh-Hans", "简体中文"),
                new OcrLanguageOption("en-US", "English"),
                new OcrLanguageOption("fr-FR", "fr-FR")
            ],
            service.AvailableLanguages);
    }

    [Fact]
    public void OcrTextNormalizerKeepsReadingOrderAndDropsBlankLines()
    {
        var text = WindowsScreenOcrService.NormalizeLines(
            ["第一行", " ", "  第二行  ", "\t", "第三行"]);

        Assert.Equal("第一行\n第二行\n第三行", text);
    }

    [Fact]
    public async Task EmptyFrameReturnsTypedFailureWithoutCallingOcr()
    {
        var engine = new FakeOcrEngine([]);
        var backend = new FakeOcrBackend
        {
            AvailableLanguages = [new OcrLanguageOption("en-US", "English")],
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = new CaptureFrame(
            new PixelRect(0, 0, 1, 1),
            4,
            [0, 0, 0, 0]);

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("OcrNoText", result.ErrorCode);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task ConfiguredLanguageUsesMatchingRecognizer()
    {
        var engine = new FakeOcrEngine(["识别结果"]);
        var backend = new FakeOcrBackend
        {
            AvailableLanguages =
            [
                new OcrLanguageOption("zh-Hans", "简体中文"),
                new OcrLanguageOption("en-US", "English")
            ],
            LanguageEngines = new Dictionary<string, FakeOcrEngine>
            {
                ["zh-Hans"] = engine
            }
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var result = await service.RecognizeAsync(
            frame,
            "zh-Hans",
            CancellationToken.None);

        Assert.Equal("识别结果", result.Text);
        Assert.Null(result.ErrorCode);
        Assert.Equal("zh-Hans", backend.LastRequestedLanguage);
        Assert.Equal(0, backend.ProfileCallCount);
    }

    [Fact]
    public async Task ProfileThenLanguageListFallbackSelectsFirstAvailableEngine()
    {
        var engine = new FakeOcrEngine(["fallback text"]);
        var backend = new FakeOcrBackend
        {
            AvailableLanguages =
            [
                new OcrLanguageOption("zh-Hans", "简体中文"),
                new OcrLanguageOption("en-US", "English")
            ],
            LanguageEngines = new Dictionary<string, FakeOcrEngine>
            {
                ["en-US"] = engine
            }
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("fallback text", result.Text);
        Assert.Equal(1, backend.ProfileCallCount);
        Assert.Equal(["zh-Hans", "en-US"], backend.RequestedLanguages);
    }

    [Fact]
    public async Task MissingConfiguredLanguageReturnsTypedUnavailableResult()
    {
        var backend = new FakeOcrBackend
        {
            AvailableLanguages = [new OcrLanguageOption("en-US", "English")]
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var result = await service.RecognizeAsync(
            frame,
            "de-DE",
            CancellationToken.None);

        Assert.Equal("OcrLanguageUnavailable", result.ErrorCode);
        Assert.Contains("语言包", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, backend.ProfileCallCount);
    }

    [Fact]
    public async Task RecognizeStripsFramePaddingAndPreservesOcrLineOrder()
    {
        var engine = new FakeOcrEngine(["first", " ", " second ", "third"]);
        var backend = new FakeOcrBackend
        {
            AvailableLanguages = [new OcrLanguageOption("en-US", "English")],
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = new CaptureFrame(
            new PixelRect(0, 0, 2, 2),
            12,
            [
                1, 2, 3, 4, 5, 6, 7, 8,
                0xAA, 0xAA, 0xAA, 0xAA,
                9, 10, 11, 12, 13, 14, 15, 16,
                0xBB, 0xBB, 0xBB, 0xBB
            ]);
        Assert.Equal(12, frame.Stride);
        Assert.Equal(2, frame.Bounds.Width);
        Assert.Equal(2, frame.Bounds.Height);
        var packed = WindowsScreenOcrService.PackBgraRows(
            frame.Bgra32.Span,
            12,
            2,
            2);
        Assert.Equal(16, packed.Length);
        Assert.Equal(
            new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16
            },
            packed);

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("first\nsecond\nthird", result.Text);
        Assert.NotNull(engine.LastBitmap);
        Assert.Equal(2, engine.LastBitmap.Width);
        Assert.Equal(2, engine.LastBitmap.Height);
        Assert.Equal(
            new byte[]
            {
                1, 2, 3, 4, 5, 6, 7, 8,
                9, 10, 11, 12, 13, 14, 15, 16
            },
            engine.LastBitmap.Bgra32);
    }

    [Fact]
    public async Task RecognitionFailureDoesNotExposeRecognizedContent()
    {
        var backend = new FakeOcrBackend
        {
            AvailableLanguages = [new OcrLanguageOption("en-US", "English")],
            ProfileEngine = new FakeOcrEngine(
                new InvalidOperationException("secret recognized text"))
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("OcrFailed", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.DoesNotContain(
            "secret",
            result.Message ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognizeHonorsCancellation()
    {
        using var service = new WindowsScreenOcrService(new FakeOcrBackend());
        using var frame = NonBlankFrame();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RecognizeAsync(frame, null, cancellation.Token));
    }

    private static CaptureFrame NonBlankFrame() =>
        new(
            new PixelRect(0, 0, 1, 1),
            4,
            [1, 2, 3, 255]);

    private sealed class FakeOcrBackend : IWindowsOcrBackend
    {
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; init; } = [];
        public IWindowsOcrEngine? ProfileEngine { get; init; }
        public IReadOnlyDictionary<string, FakeOcrEngine> LanguageEngines { get; init; } =
            new Dictionary<string, FakeOcrEngine>();
        public string? LastRequestedLanguage { get; private set; }
        public int ProfileCallCount { get; private set; }
        public List<string> RequestedLanguages { get; } = [];

        public IWindowsOcrEngine? TryCreateFromLanguage(string languageTag)
        {
            LastRequestedLanguage = languageTag;
            RequestedLanguages.Add(languageTag);
            return LanguageEngines.GetValueOrDefault(languageTag);
        }

        public IWindowsOcrEngine? TryCreateFromUserProfileLanguages()
        {
            ProfileCallCount++;
            return ProfileEngine;
        }
    }

    private sealed class FakeOcrEngine : IWindowsOcrEngine
    {
        private readonly IReadOnlyList<string>? _lines;
        private readonly Exception? _exception;

        public FakeOcrEngine(IReadOnlyList<string> lines) => _lines = lines;
        public FakeOcrEngine(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public WindowsOcrBitmap? LastBitmap { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeLinesAsync(
            WindowsOcrBitmap bitmap,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastBitmap = bitmap;
            return _exception is null
                ? Task.FromResult(_lines!)
                : Task.FromException<IReadOnlyList<string>>(_exception);
        }

        public void Dispose()
        {
        }
    }
}
