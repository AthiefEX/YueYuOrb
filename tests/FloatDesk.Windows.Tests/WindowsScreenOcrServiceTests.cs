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
    public async Task RepeatedOcrWithSameLanguageReusesOneEngine()
    {
        var engine = new FakeOcrEngine(["识别结果"]);
        var backend = new FakeOcrBackend
        {
            LanguageEngines = new Dictionary<string, FakeOcrEngine>
            {
                ["zh-Hans"] = engine
            }
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        await service.RecognizeAsync(frame, "zh-Hans", CancellationToken.None);
        await service.RecognizeAsync(frame, "zh-Hans", CancellationToken.None);

        Assert.Equal(1, backend.CreatedEngineCount);
        Assert.Equal(2, engine.CallCount);
        Assert.Equal(0, engine.DisposeCount);
    }

    [Fact]
    public async Task ChangingOcrLanguageDisposesAndRecreatesEngine()
    {
        var first = new FakeOcrEngine(["first"]);
        var second = new FakeOcrEngine(["second"]);
        var backend = new FakeOcrBackend
        {
            LanguageEngines = new Dictionary<string, FakeOcrEngine>
            {
                ["en-US"] = first,
                ["zh-Hans"] = second
            }
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        await service.RecognizeAsync(frame, "en-US", CancellationToken.None);
        await service.RecognizeAsync(frame, "zh-Hans", CancellationToken.None);

        Assert.Equal(2, backend.CreatedEngineCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, second.DisposeCount);
    }

    [Fact]
    public async Task DisposingServiceDisposesCachedEngine()
    {
        var engine = new FakeOcrEngine(["cached"]);
        var backend = new FakeOcrBackend { ProfileEngine = engine };
        var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        await service.RecognizeAsync(frame, null, CancellationToken.None);
        Assert.Equal(1, backend.CreatedEngineCount);
        Assert.Equal(0, engine.DisposeCount);

        service.Dispose();
        await WaitUntilAsync(
            () => engine.DisposeCount == 1,
            TimeSpan.FromSeconds(2));

        Assert.Equal(1, engine.DisposeCount);
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
    public async Task FailedEngineCreationIsRetriedAfterLanguageBecomesAvailable()
    {
        var engine = new FakeOcrEngine(["recovered"]);
        var backend = new RecoveringOcrBackend(engine);
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var unavailable = await service.RecognizeAsync(
            frame,
            "de-DE",
            CancellationToken.None);
        var recovered = await service.RecognizeAsync(
            frame,
            "de-DE",
            CancellationToken.None);

        Assert.Equal("OcrLanguageUnavailable", unavailable.ErrorCode);
        Assert.Null(recovered.ErrorCode);
        Assert.Equal("recovered", recovered.Text);
        Assert.Equal(2, backend.CreateAttempts);
        Assert.Equal(1, engine.CallCount);
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
    public async Task OversizedWidthIsDownscaledToMaxDimensionWithPackedStride()
    {
        var engine = new FakeOcrEngine(["scaled"]);
        var backend = new FakeOcrBackend
        {
            MaxImageDimension = 4,
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(8, 2, 40);

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("scaled", result.Text);
        Assert.NotNull(engine.LastBitmap);
        Assert.Equal(4, engine.LastBitmap.Width);
        Assert.Equal(1, engine.LastBitmap.Height);
        Assert.Equal(4 * 1 * 4, engine.LastBitmap.Bgra32.Length);
    }

    [Fact]
    public async Task OversizedHeightIsDownscaledToMaxDimensionWithPackedStride()
    {
        var engine = new FakeOcrEngine(["scaled"]);
        var backend = new FakeOcrBackend
        {
            MaxImageDimension = 4,
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(2, 8, 12);

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("scaled", result.Text);
        Assert.NotNull(engine.LastBitmap);
        Assert.Equal(1, engine.LastBitmap.Width);
        Assert.Equal(4, engine.LastBitmap.Height);
        Assert.Equal(1 * 4 * 4, engine.LastBitmap.Bgra32.Length);
    }

    [Fact]
    public async Task FramesAtOrBelowMaxDimensionAreNotUpscaled()
    {
        var engine = new FakeOcrEngine(["native"]);
        var backend = new FakeOcrBackend
        {
            MaxImageDimension = 4,
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(2, 2, 12);

        await service.RecognizeAsync(frame, null, CancellationToken.None);

        Assert.NotNull(engine.LastBitmap);
        Assert.Equal(2, engine.LastBitmap.Width);
        Assert.Equal(2, engine.LastBitmap.Height);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 4)]
    public async Task InvalidDimensionsReturnTypedFailureWithoutCallingBackend(
        int width,
        int height,
        int stride)
    {
        var backend = new FakeOcrBackend();
        using var service = new WindowsScreenOcrService(backend);
        using var frame = new CaptureFrame(
            new PixelRect(0, 0, width, height),
            stride,
            []);

        var result = await service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        Assert.Equal("OcrFailed", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Equal(0, backend.ProfileCallCount);
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

    [Fact(Timeout = 10_000)]
    public async Task ActiveCancellationDoesNotOverlapQueuedRecognition()
    {
        var engine = new BlockingOcrEngine();
        var backend = new FakeOcrBackend { ProfileEngine = engine };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();
        using var cancellation = new CancellationTokenSource();

        var canceledCall = service.RecognizeAsync(
            frame,
            null,
            cancellation.Token);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);

        var queuedCall = service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, engine.CallCount);

        engine.Release.TrySetResult(["finished"]);

        Assert.Equal(
            "finished",
            (await queuedCall.WaitAsync(TimeSpan.FromSeconds(2))).Text);
        Assert.Equal(2, engine.CallCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task BusyDisposeCompletesActiveAndQueuedCallsWithoutNewWork()
    {
        var engine = new BlockingOcrEngine();
        var backend = new FakeOcrBackend { ProfileEngine = engine };
        var service = new WindowsScreenOcrService(backend);
        using var frame = NonBlankFrame();

        var activeCall = service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queuedCall = service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);

        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => activeCall.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queuedCall.WaitAsync(TimeSpan.FromSeconds(2)));

        engine.Release.TrySetResult(["finished"]);
        await Task.Delay(50);

        Assert.Equal(1, engine.CallCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.RecognizeAsync(frame, null, CancellationToken.None));
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelledOversizedPreparationDoesNotStartWinRtOrOverlapSecondCall()
    {
        var preparer = new BlockingFramePreparer();
        var engine = new FakeOcrEngine(["second"]);
        var backend = new FakeOcrBackend
        {
            MaxImageDimension = 4,
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend, preparer);
        using var frame = Frame(64, 64, 256);
        using var cancellation = new CancellationTokenSource();

        var canceledCall = service.RecognizeAsync(
            frame,
            null,
            cancellation.Token);
        await preparer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledCall);

        var secondCall = service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, preparer.CallCount);
        Assert.Equal(0, engine.CallCount);

        preparer.ReleaseFirst.TrySetResult();

        Assert.Equal(
            "second",
            (await secondCall.WaitAsync(TimeSpan.FromSeconds(2))).Text);
        Assert.Equal(2, preparer.CallCount);
        Assert.Equal(1, engine.CallCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task DisposeDuringPreparationNeverStartsWinRtWork()
    {
        var preparer = new BlockingFramePreparer();
        var engine = new FakeOcrEngine(["unexpected"]);
        var backend = new FakeOcrBackend { ProfileEngine = engine };
        var service = new WindowsScreenOcrService(backend, preparer);
        using var frame = Frame(64, 64, 256);

        var recognition = service.RecognizeAsync(
            frame,
            null,
            CancellationToken.None);
        await preparer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => recognition.WaitAsync(TimeSpan.FromSeconds(2)));

        preparer.ReleaseFirst.TrySetResult();
        await Task.Delay(50);

        Assert.Equal(1, preparer.CallCount);
        Assert.Equal(0, engine.CallCount);
    }

    [Fact]
    public async Task EmptySmallFrameRetriesOnceWithBorderAndSameEngine()
    {
        var engine = new SmallTextOcrEngine();
        var backend = new FakeOcrBackend
        {
            ProfileEngine = engine
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(300, 24, 1204);
        var original = frame.Bgra32.ToArray();

        var result = await service.RecognizeAsync(frame, null, CancellationToken.None);

        Assert.Equal("recovered", result.Text);
        Assert.Null(result.ErrorCode);
        Assert.Equal(1, backend.ProfileCallCount);
        Assert.Equal(2, engine.Bitmaps.Count);
        Assert.Equal(300, engine.Bitmaps[0].Width);
        Assert.Equal(24, engine.Bitmaps[0].Height);
        var retry = engine.Bitmaps[1];
        Assert.Equal(632, retry.Width);
        Assert.Equal(80, retry.Height);
        Assert.Equal(new byte[] { 1, 0, 0, 255 }, retry.Bgra32[..4]);
        var firstPixel = (16 * retry.Width + 16) * 4;
        Assert.Equal(new byte[] { 1, 0, 0, 255 }, retry.Bgra32[firstPixel..(firstPixel + 4)]);
        Assert.Equal(original, frame.Bgra32.ToArray());
    }

    [Theory]
    [InlineData(300, 24, 4096, 2)]
    [InlineData(300, 24, 600, 1)]
    [InlineData(300, 200, 4096, 1)]
    [InlineData(1200, 24, 4096, 1)]
    public async Task EmptyRecognitionHasBoundedSmallImageRetry(
        int width, int height, int maxDimension, int expectedCalls)
    {
        var engine = new FakeOcrEngine([]);
        var backend = new FakeOcrBackend { ProfileEngine = engine, MaxImageDimension = maxDimension };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(width, height, width * 4);

        var result = await service.RecognizeAsync(frame, null, CancellationToken.None);

        Assert.Equal("OcrNoText", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Equal(expectedCalls, engine.CallCount);
    }

    [Fact]
    public async Task SuccessfulSmallSubtitleFramesOnlyRunOcrOnceEach()
    {
        var engine = new FakeOcrEngine(["subtitle"]);
        var backend = new FakeOcrBackend { ProfileEngine = engine };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(300, 24, 1200);

        for (var i = 0; i < 3; i++)
            Assert.Equal("subtitle", (await service.RecognizeAsync(frame, null, CancellationToken.None)).Text);

        Assert.Equal(3, engine.CallCount);
        Assert.Equal(1, backend.ProfileCallCount);
    }

    [Fact]
    public async Task EmptyConfiguredLanguageDoesNotFallBackToOtherLanguages()
    {
        var configured = new FakeOcrEngine([]);
        var other = new FakeOcrEngine(["other"]);
        var backend = new FakeOcrBackend
        {
            AvailableLanguages = [new("zh-Hans", "Chinese"), new("en-US", "English")],
            ProfileEngine = other,
            LanguageEngines = new Dictionary<string, FakeOcrEngine>
            {
                ["zh-Hans"] = configured,
                ["en-US"] = other
            }
        };
        using var service = new WindowsScreenOcrService(backend);
        using var frame = Frame(300, 24, 1200);

        var result = await service.RecognizeAsync(frame, " zh-Hans ", CancellationToken.None);

        Assert.Equal("OcrNoText", result.ErrorCode);
        Assert.Equal(2, configured.CallCount);
        Assert.Equal(0, other.CallCount);
        Assert.Equal(0, backend.ProfileCallCount);
        Assert.Equal(["zh-Hans"], backend.RequestedLanguages);
    }

    private static CaptureFrame NonBlankFrame() =>
        new(
            new PixelRect(0, 0, 1, 1),
            4,
            [1, 2, 3, 255]);

    private sealed class SmallTextOcrEngine : IWindowsOcrEngine
    {
        public List<WindowsOcrBitmap> Bitmaps { get; } = [];

        public Task<IReadOnlyList<string>> RecognizeLinesAsync(WindowsOcrBitmap bitmap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bitmaps.Add(bitmap);
            return Task.FromResult<IReadOnlyList<string>>(
                bitmap.Width >= 600 && bitmap.Height >= 48 ? ["recovered"] : []);
        }

        public void Dispose() { }
    }

    private static CaptureFrame Frame(int width, int height, int stride)
    {
        var pixels = new byte[checked(stride * height)];
        for (var row = 0; row < height; row++)
        {
            pixels[row * stride] = 1;
            pixels[(row * stride) + 3] = 255;
        }
        return new CaptureFrame(
            new PixelRect(0, 0, width, height),
            stride,
            pixels);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeOcrBackend : IWindowsOcrBackend
    {
        public int MaxImageDimension { get; init; } = 4096;
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; init; } = [];
        public IWindowsOcrEngine? ProfileEngine { get; init; }
        public IReadOnlyDictionary<string, FakeOcrEngine> LanguageEngines { get; init; } =
            new Dictionary<string, FakeOcrEngine>();
        public string? LastRequestedLanguage { get; private set; }
        public int ProfileCallCount { get; private set; }
        public int CreatedEngineCount { get; private set; }
        public List<string> RequestedLanguages { get; } = [];

        public IWindowsOcrEngine? TryCreateFromLanguage(string languageTag)
        {
            LastRequestedLanguage = languageTag;
            RequestedLanguages.Add(languageTag);
            var engine = LanguageEngines.GetValueOrDefault(languageTag);
            if (engine is not null)
                CreatedEngineCount++;
            return engine;
        }

        public IWindowsOcrEngine? TryCreateFromUserProfileLanguages()
        {
            ProfileCallCount++;
            if (ProfileEngine is not null)
                CreatedEngineCount++;
            return ProfileEngine;
        }
    }

    private sealed class RecoveringOcrBackend(
        IWindowsOcrEngine engine) : IWindowsOcrBackend
    {
        public int MaxImageDimension => 4096;
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages => [];
        public int CreateAttempts { get; private set; }

        public IWindowsOcrEngine? TryCreateFromLanguage(string languageTag)
        {
            CreateAttempts++;
            return CreateAttempts >= 2 ? engine : null;
        }

        public IWindowsOcrEngine? TryCreateFromUserProfileLanguages() => null;
    }

    private sealed class FakeOcrEngine : IWindowsOcrEngine
    {
        private readonly IReadOnlyList<string>? _lines;
        private readonly Exception? _exception;

        public FakeOcrEngine(IReadOnlyList<string> lines) => _lines = lines;
        public FakeOcrEngine(Exception exception) => _exception = exception;

        public int CallCount { get; private set; }
        public int DisposeCount { get; private set; }
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
            DisposeCount++;
        }
    }

    private sealed class BlockingOcrEngine : IWindowsOcrEngine
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<string>> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<string>> RecognizeLinesAsync(
            WindowsOcrBitmap bitmap,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            return Release.Task;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingFramePreparer : IWindowsOcrFramePreparer
    {
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public WindowsOcrBitmap Prepare(
            WindowsOcrFrame frame,
            int maxImageDimension,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                FirstStarted.TrySetResult();
                ReleaseFirst.Task.GetAwaiter().GetResult();
            }
            return new WindowsOcrBitmap(1, 1, [1, 2, 3, 255]);
        }
    }
}
