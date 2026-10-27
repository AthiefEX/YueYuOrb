using FloatDesk.App.Capture;
using FloatDesk.App.Coordination;
using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Ui.Tests;

public sealed class SubtitleTranslationControllerTests
{
    private static readonly TranslationCaptureRegion Region =
        new("monitor-1", 0.1, 0.7, 0.8, 0.2);

    [Fact]
    public async Task SubtitleLoopSkipsStableFrameAfterFirstTranslation()
    {
        var source = new SequenceSubtitleSource(
            [0x80],
            finishedAfterCaptures: 3);
        var ocr = new SequenceOcr(
            new ScreenOcrResult("Hello subtitle", null, null));
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await source.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.StopAsync();

        Assert.Equal(1, host.TranslateCount);
        Assert.Equal(TranslationPurpose.Subtitle, host.LastPurpose);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task ChangedFrameIsRecapturedAfterStabilityDelayBeforeOcr()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0, 0xE0],
            finishedAfterCaptures: 3);
        var ocr = new SequenceOcr(
            new ScreenOcrResult("Changed subtitle", null, null));
        var host = new RecordingSubtitleHost();
        var clock = new RecordingClock();
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            clock);

        await controller.StartAsync(Settings(), CancellationToken.None);
        await source.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.StopAsync();

        Assert.True(source.CaptureCount >= 3);
        Assert.Equal(0xE0, ocr.LastFrameValue);
        Assert.Contains(TimeSpan.FromMilliseconds(90), clock.Delays);
        Assert.Equal(1, host.TranslateCount);
    }

    [Fact]
    public async Task NearDuplicateTextSkipsRedundantTranslation()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0, 0x10, 0xE0],
            finishedAfterCaptures: 4);
        var ocr = new SequenceOcr(
            new ScreenOcrResult("Hello subtitle.", null, null),
            new ScreenOcrResult("Hello subtitle!", null, null));
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await source.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.StopAsync();

        Assert.Equal(2, ocr.CallCount);
        Assert.Equal(1, host.TranslateCount);
    }

    [Fact]
    public async Task PauseCancelsInFlightOcrWithoutEndingSubtitleSession()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var ocr = new BlockingOcr();
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await ocr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        controller.Pause("字幕翻译已暂停。");
        await ocr.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(controller.IsRunning);
        Assert.True(controller.IsPaused);
        Assert.True(host.IsPaused);
        await controller.StopAsync();
        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task AuthenticationFailurePausesAutomaticRetries()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0, 0x10, 0xE0],
            finishedAfterCaptures: 4);
        var ocr = new SequenceOcr(
            new ScreenOcrResult("Hello subtitle", null, null));
        var host = new RecordingSubtitleHost
        {
            TranslationResult = TranslationResponse.Fail(
                "Hello subtitle",
                "en",
                "Test provider",
                TranslationFailureKind.Authentication,
                "Unauthorized",
                "认证失败。")
        };
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await host.TranslationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => controller.IsPaused,
            TimeSpan.FromSeconds(2));

        Assert.True(controller.IsPaused);
        Assert.Equal(1, host.TranslateCount);
        await controller.StopAsync();
    }

    [Fact]
    public async Task NetworkFailureRetriesTheSameSubtitleAfterBackoff()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var ocr = new SequenceOcr(
            new ScreenOcrResult("Hello subtitle", null, null));
        var host = new RecordingSubtitleHost();
        host.TranslationResults.Enqueue(
            TranslationResponse.Fail(
                "Hello subtitle",
                "en",
                "Test provider",
                TranslationFailureKind.Network,
                "Offline",
                "网络连接不可用。"));
        host.TranslationResults.Enqueue(
            TranslationResponse.Ok(
                "Hello subtitle",
                "翻译",
                "en",
                "Test provider"));
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await host.RetryAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await controller.StopAsync();

        Assert.True(host.TranslateCount >= 2);
    }

    [Fact(Timeout = 10_000)]
    public async Task SlowTranslationDoesNotBlockNextCaptureOrOcr()
    {
        var source = new AlternatingSubtitleSource();
        var ocr = new CallbackOcr((call, _) =>
            Task.FromResult(
                new ScreenOcrResult(
                    $"Subtitle {call}",
                    null,
                    null)));
        var firstTranslationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstTranslation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTranslation = 1;
        var host = new RecordingSubtitleHost
        {
            TranslationHandler = async (text, _) =>
            {
                if (Interlocked.Exchange(ref firstTranslation, 0) == 1)
                {
                    firstTranslationEntered.TrySetResult();
                    await releaseFirstTranslation.Task
                        .ConfigureAwait(false);
                }

                return TranslationResponse.Ok(
                    text,
                    "译文",
                    "en",
                    "Test provider");
            }
        };
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await firstTranslationEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => source.CaptureCount >= 6 && ocr.CallCount >= 3,
            TimeSpan.FromSeconds(2));

        Assert.False(releaseFirstTranslation.Task.IsCompleted);
        releaseFirstTranslation.TrySetResult();
        await controller.StopAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task NewCaptionCancelsPreviousTranslation()
    {
        var source = new AlternatingSubtitleSource();
        var ocr = new CallbackOcr((call, _) =>
            Task.FromResult(
                new ScreenOcrResult(
                    call == 1 ? "First subtitle" : "Second subtitle",
                    null,
                    null)));
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingSubtitleHost
        {
            TranslationHandler = async (text, cancellationToken) =>
            {
                if (text == "First subtitle")
                {
                    firstStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        firstCancelled.TrySetResult();
                        return TranslationResponse.Fail(
                            text,
                            "en",
                            "Test provider",
                            TranslationFailureKind.Cancelled,
                            "Cancelled",
                            "已取消。");
                    }
                }

                if (text == "Second subtitle")
                    secondAttempted.TrySetResult();
                return TranslationResponse.Ok(
                    text,
                    "译文",
                    "en",
                    "Test provider");
            }
        };
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(host.TranslateCount >= 2);
        Assert.Contains("First subtitle", host.TranslatedTexts);
        Assert.Contains("Second subtitle", host.TranslatedTexts);
        await controller.StopAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task LateOcrForSupersededFrameDoesNotTranslate()
    {
        var source = new AlternatingSubtitleSource();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ocr = new CallbackOcr(async (call, _) =>
        {
            if (call == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
                return new ScreenOcrResult("Old subtitle", null, null);
            }

            secondEntered.TrySetResult();
            return new ScreenOcrResult("New subtitle", null, null);
        });
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            ocr,
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => source.CaptureCount >= 4,
            TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(
            () => host.TranslatedTexts.Contains(
                "New subtitle",
                StringComparer.Ordinal),
            TimeSpan.FromSeconds(2));
        await controller.StopAsync();

        Assert.DoesNotContain("Old subtitle", host.TranslatedTexts);
        Assert.Contains("New subtitle", host.TranslatedTexts);
    }

    [Fact]
    public async Task SavedRegionCaptureUsesOnlyTheRequestedCrop()
    {
        var monitor = new DisplayMonitor(
            "monitor-1",
            new PixelRect(100, 50, 100, 100),
            new PixelRect(100, 50, 100, 90),
            IsPrimary: true);
        var capture = new RecordingScreenCapture();
        var flushCount = 0;
        using var selection = new RegionSelectionCoordinator();
        using var service = new TranslationRegionCaptureService(
            new CaptureSessionGate(),
            () => throw new InvalidOperationException(
                "Fixed-region capture must not hide windows."),
            selection,
            capture,
            () => new PixelRect(0, 0, 200, 200),
            () => [monitor],
            () => flushCount++,
            diagnostics: null,
            ownsCapture: false);
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.2,
            0.3,
            0.4);

        var captured = await service.CaptureRegionAsync(
            region,
            CancellationToken.None);

        Assert.NotNull(captured);
        using var frame = captured!.Frame;
        Assert.Equal(new PixelRect(110, 70, 30, 40), captured.Bounds);
        Assert.Equal(region, captured.Region);
        Assert.Equal(captured.Bounds, frame.Bounds);
        Assert.Equal(1, capture.CallCount);
        Assert.Equal(1, flushCount);
    }

    [Fact]
    public async Task UnsafeOverlayPlacementPausesBeforeSubtitleSampling()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var host = new RecordingSubtitleHost
        {
            PlacementStatus =
                TranslationSubtitlePlacementStatus.NoSafePlacement
        };
        var controller = new SubtitleTranslationController(
            new SequenceOcr(
                new ScreenOcrResult("Hello subtitle", null, null)),
            source,
            host,
            new ImmediateClock());

        await controller.StartAsync(Settings(), CancellationToken.None);
        await WaitUntilAsync(
            () => controller.IsPaused,
            TimeSpan.FromSeconds(2));

        Assert.Equal(0, source.CaptureCount);
        Assert.Contains(
            "重新选择字幕区域",
            host.LastStatus,
            StringComparison.Ordinal);
        await controller.StopAsync();
    }

    [Fact]
    public async Task DisplayChangePausesWhenSavedMonitorDisappears()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            new SequenceOcr(
                new ScreenOcrResult("Hello subtitle", null, null)),
            source,
            host,
            new ImmediateClock());
        await controller.StartAsync(Settings(), CancellationToken.None);
        host.PlacementStatus =
            TranslationSubtitlePlacementStatus.MonitorMissing;

        controller.RefreshPlacement();
        await WaitUntilAsync(
            () => controller.IsPaused,
            TimeSpan.FromSeconds(2));

        Assert.Contains(
            "显示器不可用",
            host.LastStatus,
            StringComparison.Ordinal);
        await controller.StopAsync();
    }

    [Fact]
    public async Task ResumeRechecksPlacementBeforeRestartingSamples()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var host = new RecordingSubtitleHost
        {
            PlacementStatus =
                TranslationSubtitlePlacementStatus.MonitorMissing
        };
        var controller = new SubtitleTranslationController(
            new SequenceOcr(
                new ScreenOcrResult("Hello subtitle", null, null)),
            source,
            host,
            new ImmediateClock());
        await controller.StartAsync(Settings(), CancellationToken.None);
        await WaitUntilAsync(
            () => controller.IsPaused,
            TimeSpan.FromSeconds(2));

        controller.Resume();

        Assert.True(controller.IsPaused);
        Assert.Equal(0, source.CaptureCount);
        await controller.StopAsync();
    }

    [Fact]
    public async Task ResumeRenewsOperationReplacedByInteractiveRoute()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            new SequenceOcr(
                new ScreenOcrResult("Hello subtitle", null, null)),
            source,
            host,
            new ImmediateClock());
        await controller.StartAsync(Settings(), CancellationToken.None);

        controller.Pause("截图进行中。");
        host.OperationActive = false;
        controller.Resume();

        Assert.False(controller.IsPaused);
        Assert.Equal(2, host.SubtitleStatusCount);
        Assert.True(host.OperationActive);
        await controller.StopAsync();
    }

    [Fact]
    public async Task RenewedOperationReappliesCaptureSafePlacementBeforeSampling()
    {
        var source = new SequenceSubtitleSource(
            [0x10, 0xE0],
            finishedAfterCaptures: null);
        var host = new RecordingSubtitleHost();
        var controller = new SubtitleTranslationController(
            new SequenceOcr(
                new ScreenOcrResult("Hello subtitle", null, null)),
            source,
            host,
            new ImmediateClock());
        await controller.StartAsync(Settings(), CancellationToken.None);

        controller.Pause("截图进行中。");
        host.OperationActive = false;
        host.FailSafePlacementWhenActive = true;
        controller.Resume();

        Assert.True(controller.IsPaused);
        Assert.Contains(
            "重新选择字幕区域",
            host.LastStatus,
            StringComparison.Ordinal);
        await controller.StopAsync();
    }

    private static TranslationSettings Settings() =>
        new(
            TranslationProviderKind.MyMemory,
            "auto",
            "zh-Hans",
            "",
            "",
            "")
        {
            SubtitleRefreshIntervalMs = 300,
            SubtitleRegion = Region
        };

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

    private sealed class SequenceSubtitleSource(
        IReadOnlyList<byte> values,
        int? finishedAfterCaptures) : ITranslationSubtitleCapture
    {
        private int _captureCount;

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CaptureCount => Volatile.Read(ref _captureCount);

        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _captureCount);
            if (finishedAfterCaptures is { } finishAt &&
                call >= finishAt)
            {
                Finished.TrySetResult();
            }
            var value = values[Math.Min(call - 1, values.Count - 1)];
            var frame = new CaptureFrame(
                new PixelRect(0, 0, 4, 4),
                16,
                Enumerable.Repeat(value, 4 * 4 * 4).ToArray());
            return Task.FromResult<CapturedTranslationRegion?>(
                new CapturedTranslationRegion(
                    frame,
                    new PixelRect(0, 0, 4, 4),
                    region));
        }
    }

    private sealed class AlternatingSubtitleSource : ITranslationSubtitleCapture
    {
        private int _captureCount;

        public int CaptureCount => Volatile.Read(ref _captureCount);

        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion region,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _captureCount);
            var value = (call & 1) == 0 ? (byte)0x10 : (byte)0xE0;
            var frame = new CaptureFrame(
                new PixelRect(0, 0, 4, 4),
                16,
                Enumerable.Repeat(value, 4 * 4 * 4).ToArray());
            return Task.FromResult<CapturedTranslationRegion?>(
                new CapturedTranslationRegion(
                    frame,
                    new PixelRect(0, 0, 4, 4),
                    region));
        }
    }

    private sealed class CallbackOcr(
        Func<int, CancellationToken, Task<ScreenOcrResult>> recognize)
        : IScreenOcrService
    {
        private int _callCount;

        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            return recognize(call, cancellationToken);
        }
    }

    private sealed class SequenceOcr(
        params ScreenOcrResult[] results) : IScreenOcrService
    {
        private int _callCount;

        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];
        public int CallCount => Volatile.Read(ref _callCount);
        public byte LastFrameValue { get; private set; }

        public Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            LastFrameValue = frame.Bgra32.Span[0];
            return Task.FromResult(
                results[Math.Min(call - 1, results.Length - 1)]);
        }
    }

    private sealed class BlockingOcr : IScreenOcrService
    {
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            return new ScreenOcrResult("unused", null, null);
        }
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            lock (Delays)
                Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSubtitleHost : ITranslationRouteHost
    {
        public TranslationResponse TranslationResult { get; init; } =
            TranslationResponse.Ok(
                "source",
                "翻译",
                "en",
                "Test provider");
        public TaskCompletionSource TranslationAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RetryAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Queue<TranslationResponse> TranslationResults { get; } = [];
        public List<string> TranslatedTexts { get; } = [];
        public Func<string, CancellationToken, Task<TranslationResponse>>?
            TranslationHandler { get; init; }
        private int _translateCount;
        public int TranslateCount => Volatile.Read(ref _translateCount);
        public TranslationPurpose? LastPurpose { get; private set; }
        public bool IsPaused { get; private set; }
        public int SubtitleStatusCount { get; private set; }
        public bool OperationActive { get; set; } = true;
        public bool FailSafePlacementWhenActive { get; set; }
        public string LastStatus { get; private set; } = "";
        public List<string> SubtitleTexts { get; } = [];
        public List<string> SubtitleNotices { get; } = [];
        public TranslationSubtitlePlacementStatus PlacementStatus { get; set; } =
            TranslationSubtitlePlacementStatus.Ready;

        public void ApplySettings(AppSettings settings)
        {
        }

        public TranslationRouteHandle ShowSubtitleStatus(
            PixelPoint anchor,
            string status)
        {
            SubtitleStatusCount++;
            OperationActive = true;
            return new TranslationRouteHandle(
                new TranslationOverlayOperationToken(SubtitleStatusCount),
                anchor);
        }

        public TranslationRouteHandle ShowStatus(
            PixelPoint anchor,
            string status) =>
            ShowSubtitleStatus(anchor, status);

        public void ShowStatus(
            TranslationRouteHandle operation,
            string status)
        {
            LastStatus = status;
        }

        public void ShowResult(
            TranslationRouteHandle operation,
            TranslationResponse response)
        {
        }

        public void ShowError(
            TranslationRouteHandle operation,
            string message)
        {
        }

        public TranslationSubtitlePlacementStatus ShowSubtitleText(
            TranslationRouteHandle operation,
            string translatedText)
        {
            SubtitleTexts.Add(translatedText);
            return PlacementStatus;
        }

        public void ShowSubtitleNotice(
            TranslationRouteHandle operation,
            string notice)
        {
            SubtitleNotices.Add(notice);
        }

        public void SetSubtitleControls(
            TranslationRouteHandle operation,
            bool active,
            bool paused,
            bool canReselect)
        {
            IsPaused = active && paused;
        }

        public TranslationSubtitlePlacementStatus PrepareSubtitleCaptureRegion(
            TranslationCaptureRegion region) =>
            FailSafePlacementWhenActive && OperationActive
                ? TranslationSubtitlePlacementStatus.NoSafePlacement
                : PlacementStatus;

        public bool IsOperationActive(TranslationRouteHandle operation) =>
            OperationActive;

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _translateCount);
            lock (TranslatedTexts)
                TranslatedTexts.Add(text);
            LastPurpose = purpose;
            TranslationAttempted.TrySetResult();
            if (count >= 2)
                RetryAttempted.TrySetResult();
            if (TranslationHandler is { } handler)
                return handler(text, cancellationToken);
            return Task.FromResult(
                TranslationResults.Count > 0
                    ? TranslationResults.Dequeue()
                    : TranslationResult);
        }

        public void DismissActiveOperation()
        {
        }
    }

    private sealed class RecordingScreenCapture : IScreenCapture
    {
        public int CallCount { get; private set; }

        public Task<CaptureFrame> CaptureAsync(
            PixelRect bounds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(
                new CaptureFrame(
                    bounds,
                    checked(bounds.Width * 4),
                    new byte[checked(bounds.Width * bounds.Height * 4)]));
        }
    }
}
