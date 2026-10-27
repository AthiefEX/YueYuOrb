using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class SubtitleTranslationController : IDisposable
{
    private static readonly TimeSpan StabilityDelay =
        TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan FailureBackoff =
        TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly IScreenOcrService _ocr;
    private readonly ITranslationSubtitleCapture _capture;
    private readonly ITranslationRouteHost _host;
    private readonly IClock _clock;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _captureIterationCancellation;
    private CancellationTokenSource? _ocrIterationCancellation;
    private CancellationTokenSource? _translationCancellation;
    private TaskCompletionSource? _resumeSignal;
    private TaskCompletionSource? _firstCandidateObserved;
    private TaskCompletionSource? _translationStartedSignal;
    private Task? _captureTask;
    private Task? _ocrTask;
    private Task? _translationTask;
    private LatestSubtitleWorkSlot<SubtitleOcrWork>? _ocrWork;
    private TranslationRouteHandle? _operation;
    private TranslationSettings? _settings;
    private FrameSignature? _previousSignature;
    private string? _previousText;
    private string? _activeTranslationText;
    private long _generation;
    private long _latestPublishedSequence;
    private long _activeTranslationSequence;
    private bool _running;
    private bool _paused;
    private bool _placementFailurePaused;
    private bool _disposed;

    public SubtitleTranslationController(
        IScreenOcrService ocr,
        ITranslationSubtitleCapture capture,
        ITranslationRouteHost host,
        IClock clock)
    {
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _capture = capture ??
            throw new ArgumentNullException(nameof(capture));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _running;
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return _running && _paused;
        }
    }

    public async Task StartAsync(
        TranslationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SubtitleRegion is null)
        {
            throw new ArgumentException(
                "A subtitle capture region is required.",
                nameof(settings));
        }

        await StopAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var work = new LatestSubtitleWorkSlot<SubtitleOcrWork>();
        long generation;
        TranslationRouteHandle operation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = NextGeneration(_generation);
            _generation = generation;
            _settings = settings;
            _previousSignature = null;
            _previousText = null;
            _activeTranslationText = null;
            _latestPublishedSequence = 0;
            _activeTranslationSequence = 0;
            _paused = false;
            _placementFailurePaused = false;
            _resumeSignal = null;
            _firstCandidateObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _translationStartedSignal = null;
            _running = true;
            _sessionCancellation = session;
            _ocrWork = work;
            operation = _host.ShowSubtitleStatus(
                TranslationRoutePoint.GetCursorPosition(),
                "字幕翻译准备中。");
            _operation = operation;
        }

        _host.SetSubtitleControls(
            operation,
            active: true,
            paused: false,
            canReselect: true);

        var placement = _host.PrepareSubtitleCaptureRegion(
            settings.SubtitleRegion);
        if (placement != TranslationSubtitlePlacementStatus.Ready)
            PauseForPlacementFailure(placement);

        lock (_sync)
        {
            if (!IsCurrentGenerationLocked(generation))
            {
                CancelQuietly(session);
                work.Dispose();
            }
            else
            {
                _captureTask = Task.Run(
                    () => RunCaptureLoopAsync(
                        generation,
                        settings,
                        session,
                        work),
                    CancellationToken.None);
                _ocrTask = Task.Run(
                    () => RunOcrLoopAsync(
                        generation,
                        settings,
                        session,
                        work),
                    CancellationToken.None);
            }
        }
    }

    public async Task StopAsync()
    {
        Task? capture;
        Task? ocr;
        Task? translation;
        CancellationTokenSource? captureIteration;
        CancellationTokenSource? ocrIteration;
        CancellationTokenSource? translationCancellation;
        CancellationTokenSource? session;
        LatestSubtitleWorkSlot<SubtitleOcrWork>? work;
        lock (_sync)
        {
            if (!_running &&
                _captureTask is null &&
                _ocrTask is null &&
                _translationTask is null &&
                _ocrWork is null)
            {
                return;
            }

            _running = false;
            _paused = false;
            _placementFailurePaused = false;
            _activeTranslationText = null;
            _previousSignature = null;
            _previousText = null;

            capture = _captureTask;
            ocr = _ocrTask;
            translation = _translationTask;
            _captureTask = null;
            _ocrTask = null;
            _translationTask = null;

            captureIteration = _captureIterationCancellation;
            ocrIteration = _ocrIterationCancellation;
            translationCancellation = _translationCancellation;
            _captureIterationCancellation = null;
            _ocrIterationCancellation = null;
            _translationCancellation = null;

            session = _sessionCancellation;
            _sessionCancellation = null;
            work = _ocrWork;
            _ocrWork = null;
            ResumeWaiterLocked();
            ReleaseFirstCandidateObservationLocked();
            ReleaseTranslationStartedLocked();
        }

        CancelQuietly(session);
        CancelQuietly(captureIteration);
        CancelQuietly(ocrIteration);
        CancelQuietly(translationCancellation);
        work?.Dispose();

        await AwaitTaskAsync(capture).ConfigureAwait(false);
        await AwaitTaskAsync(ocr).ConfigureAwait(false);
        await AwaitTaskAsync(translation).ConfigureAwait(false);
        session?.Dispose();
    }

    public void Pause(string reason) =>
        PauseCore(reason, placementFailure: false);

    private void PauseCore(string reason, bool placementFailure)
    {
        TranslationRouteHandle? operation;
        CancellationTokenSource? captureIteration;
        CancellationTokenSource? ocrIteration;
        CancellationTokenSource? translationCancellation;
        Task? abandonedTranslation = null;
        lock (_sync)
        {
            if (!_running || (_paused && !placementFailure))
                return;

            var wasPaused = _paused;
            _paused = true;
            _placementFailurePaused = placementFailure;
            if (!wasPaused)
            {
                _resumeSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _previousSignature = null;
                _activeTranslationText = null;

                captureIteration = _captureIterationCancellation;
                ocrIteration = _ocrIterationCancellation;
                translationCancellation = _translationCancellation;
                _captureIterationCancellation = null;
                _ocrIterationCancellation = null;
                _translationCancellation = null;
                ReleaseTranslationStartedLocked();
                abandonedTranslation = _translationTask;
                _translationTask = null;
            }
            else
            {
                captureIteration = null;
                ocrIteration = null;
                translationCancellation = null;
            }

            operation = _operation;
        }

        CancelQuietly(captureIteration);
        CancelQuietly(ocrIteration);
        CancelQuietly(translationCancellation);
        ObserveAbandonedTask(abandonedTranslation);

        if (operation is { } current)
        {
            _host.SetSubtitleControls(
                current,
                active: true,
                paused: true,
                canReselect: true);
            _host.ShowStatus(
                current,
                string.IsNullOrWhiteSpace(reason)
                    ? "字幕翻译已暂停。"
                    : reason);
        }
    }

    public void Resume()
    {
        TranslationSettings? settings;
        lock (_sync)
        {
            if (!_running || !_paused)
                return;
            settings = _settings;
        }

        if (settings?.SubtitleRegion is { } region)
        {
            var placement = _host.PrepareSubtitleCaptureRegion(region);
            if (placement != TranslationSubtitlePlacementStatus.Ready)
            {
                PauseForPlacementFailure(placement);
                return;
            }
        }

        long generation;
        TranslationRouteHandle? operation;
        lock (_sync)
        {
            if (!_running || !_paused)
                return;
            generation = _generation;
            operation = _operation;
        }

        var renewed = false;
        if (operation is { } existing &&
            !_host.IsOperationActive(existing))
        {
            var fresh = _host.ShowSubtitleStatus(
                TranslationRoutePoint.GetCursorPosition(),
                "字幕翻译已继续。");
            var accepted = false;
            lock (_sync)
            {
                if (IsCurrentGenerationLocked(generation))
                {
                    _operation = fresh;
                    operation = fresh;
                    accepted = true;
                }
            }
            if (!accepted)
            {
                _host.SetSubtitleControls(
                    fresh,
                    active: false,
                    paused: false,
                    canReselect: false);
                return;
            }
            renewed = true;
        }

        var resumed = false;
        if (operation is { } current)
        {
            _host.SetSubtitleControls(
                current,
                active: true,
                paused: false,
                canReselect: true);
            if (settings?.SubtitleRegion is { } activeRegion)
            {
                var activePlacement =
                    _host.PrepareSubtitleCaptureRegion(activeRegion);
                if (activePlacement !=
                    TranslationSubtitlePlacementStatus.Ready)
                {
                    PauseForPlacementFailure(activePlacement);
                    return;
                }
            }
            lock (_sync)
            {
                if (IsCurrentGenerationLocked(generation) && _paused)
                {
                    _paused = false;
                    _placementFailurePaused = false;
                    ResumeWaiterLocked();
                    ReleaseFirstCandidateObservationLocked();
                    resumed = true;
                }
            }
            if (resumed && !renewed)
                _host.ShowStatus(current, "字幕翻译已继续。");
        }
    }

    public void RefreshPlacement()
    {
        TranslationSettings? settings;
        lock (_sync)
        {
            if (!_running)
                return;
            if (_paused && !_placementFailurePaused)
                return;
            settings = _settings;
        }
        if (settings?.SubtitleRegion is not { } region)
            return;

        var placement = _host.PrepareSubtitleCaptureRegion(region);
        if (placement != TranslationSubtitlePlacementStatus.Ready)
        {
            PauseForPlacementFailure(placement);
            return;
        }

        bool shouldResume;
        lock (_sync)
        {
            shouldResume =
                _running &&
                _paused &&
                _placementFailurePaused;
        }
        if (shouldResume)
            Resume();
    }

    public async Task ReselectAsync(
        TranslationSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SubtitleRegion is null)
        {
            throw new ArgumentException(
                "A subtitle capture region is required.",
                nameof(settings));
        }

        await StopAsync().ConfigureAwait(false);
        await StartAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public void UpdateSettings(TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SubtitleRegion is null)
            return;
        lock (_sync)
        {
            if (_running)
                _settings = settings;
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? session;
        CancellationTokenSource? captureIteration;
        CancellationTokenSource? ocrIteration;
        CancellationTokenSource? translationCancellation;
        LatestSubtitleWorkSlot<SubtitleOcrWork>? work;
        Task? capture;
        Task? ocr;
        Task? translation;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _running = false;
            _paused = false;
            _placementFailurePaused = false;
            _activeTranslationText = null;
            session = _sessionCancellation;
            _sessionCancellation = null;
            captureIteration = _captureIterationCancellation;
            ocrIteration = _ocrIterationCancellation;
            translationCancellation = _translationCancellation;
            _captureIterationCancellation = null;
            _ocrIterationCancellation = null;
            _translationCancellation = null;
            work = _ocrWork;
            _ocrWork = null;
            capture = _captureTask;
            ocr = _ocrTask;
            translation = _translationTask;
            _captureTask = null;
            _ocrTask = null;
            _translationTask = null;
            ResumeWaiterLocked();
            ReleaseFirstCandidateObservationLocked();
            ReleaseTranslationStartedLocked();
        }

        CancelQuietly(session);
        CancelQuietly(captureIteration);
        CancelQuietly(ocrIteration);
        CancelQuietly(translationCancellation);
        work?.Dispose();
        ObserveAbandonedTask(capture);
        ObserveAbandonedTask(ocr);
        ObserveAbandonedTask(translation);
        session?.Dispose();
    }

    private async Task RunCaptureLoopAsync(
        long generation,
        TranslationSettings settings,
        CancellationTokenSource session,
        LatestSubtitleWorkSlot<SubtitleOcrWork> work)
    {
        var cancellationToken = session.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitWhilePausedAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    break;

                var currentSettings = GetCurrentSettings(generation)
                    ?? settings;
                var iteration = CancellationTokenSource
                    .CreateLinkedTokenSource(cancellationToken);
                lock (_sync)
                {
                    if (!IsCurrentGenerationLocked(generation))
                    {
                        iteration.Dispose();
                        break;
                    }
                    _captureIterationCancellation = iteration;
                }

                try
                {
                    using var first = await CaptureSignatureAsync(
                            currentSettings.SubtitleRegion!,
                            iteration.Token)
                        .ConfigureAwait(false);
                    if (first is null)
                        continue;

                    FrameSignature? previous;
                    lock (_sync)
                        previous = _previousSignature;
                    if (!SubtitleFrameChangeDetector
                            .HasMeaningfulChange(previous, first.Signature))
                    {
                        SetPreviousSignature(
                            generation,
                            first.Signature);
                        continue;
                    }

                    await _clock.DelayAsync(
                            StabilityDelay,
                            iteration.Token)
                        .ConfigureAwait(false);
                    var stable = await CaptureSignatureAsync(
                            currentSettings.SubtitleRegion!,
                            iteration.Token)
                        .ConfigureAwait(false);
                    if (stable is null)
                        continue;

                    SetPreviousSignature(
                        generation,
                        stable.Signature);
                    var publishedCandidate = false;
                    long publishedSequence = 0;
                    lock (_sync)
                    {
                        if (IsCurrentUnpausedGenerationLocked(generation))
                        {
                            var sequence = NextGeneration(
                                _latestPublishedSequence);
                            _latestPublishedSequence = sequence;
                            work.Publish(
                                new SubtitleOcrWork(
                                    generation,
                                    sequence,
                                    currentSettings.OcrLanguage,
                                    stable));
                            publishedCandidate = true;
                            publishedSequence = sequence;
                        }
                        else
                        {
                            stable.Dispose();
                        }
                    }
                    if (publishedCandidate)
                    {
                        if (publishedSequence == 1)
                        {
                            Task? firstObserved;
                            lock (_sync)
                                firstObserved =
                                    _firstCandidateObserved?.Task;
                            if (firstObserved is not null)
                            {
                                await firstObserved.WaitAsync(
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException)
                    when (iteration.IsCancellationRequested ||
                          cancellationToken.IsCancellationRequested)
                {
                }
                catch (InvalidOperationException)
                {
                    PauseForPlacementFailure(
                        TranslationSubtitlePlacementStatus.MonitorMissing);
                }
                catch
                {
                    ShowStatusIfCurrent(
                        generation,
                        "字幕采样失败，稍后重试。");
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(
                            _captureIterationCancellation,
                            iteration))
                        {
                            _captureIterationCancellation = null;
                        }
                    }
                    iteration.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                var delay = GetCaptureDelay(currentSettings);
                using var delayCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                lock (_sync)
                    _captureIterationCancellation = delayCancellation;
                try
                {
                    await _clock.DelayAsync(
                            delay,
                            delayCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (delayCancellation.IsCancellationRequested ||
                          cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(
                            _captureIterationCancellation,
                            delayCancellation))
                        {
                            _captureIterationCancellation = null;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteSession(generation, session);
        }
    }

    private async Task RunOcrLoopAsync(
        long generation,
        TranslationSettings settings,
        CancellationTokenSource session,
        LatestSubtitleWorkSlot<SubtitleOcrWork> work)
    {
        var cancellationToken = session.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SubtitleOcrWork? currentWork;
                try
                {
                    currentWork = await work.TakeAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (currentWork is null)
                {
                    ReleaseFirstCandidateObservation();
                    break;
                }

                try
                {
                    await WaitWhilePausedAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!IsCurrentWork(generation, currentWork.Sequence))
                    {
                        ReleaseFirstCandidateObservation();
                        continue;
                    }

                    var iteration = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    lock (_sync)
                    {
                        if (!IsCurrentWorkLocked(
                                generation,
                                currentWork.Sequence))
                        {
                            iteration.Dispose();
                            ReleaseFirstCandidateObservation();
                            continue;
                        }
                        _ocrIterationCancellation = iteration;
                    }

                    try
                    {
                        Task<ScreenOcrResult> ocrRequest;
                        try
                        {
                            ocrRequest = _ocr.RecognizeAsync(
                                currentWork.Frame,
                                currentWork.LanguageTag ??
                                    settings.OcrLanguage,
                                iteration.Token);
                        }
                        finally
                        {
                            ReleaseFirstCandidateObservation();
                        }
                        var ocrResult = await ocrRequest
                            .ConfigureAwait(false);
                        if (iteration.IsCancellationRequested)
                            continue;
                        if (!IsCurrentWork(
                                generation,
                                currentWork.Sequence))
                        {
                            continue;
                        }

                        if (ocrResult.ErrorCode is { Length: > 0 } errorCode)
                        {
                            if (errorCode == "OcrLanguageUnavailable")
                            {
                                Pause(
                                    "未安装可用的 OCR 语言包，请先在 Windows 设置中安装对应语言。");
                            }
                            else
                            {
                                ShowStatusIfCurrent(
                                    generation,
                                    errorCode == "OcrNoText"
                                        ? "未识别到字幕文字。"
                                        : "无法识别字幕文字，稍后重试。");
                            }
                            continue;
                        }

                        var text = TranslationTextProcessor.Normalize(
                            ocrResult.Text ?? "");
                        if (text.Length == 0)
                        {
                            ShowStatusIfCurrent(
                                generation,
                                "未识别到字幕文字。");
                            continue;
                        }
                        if (IsDuplicateText(text))
                            continue;

                        ShowStatusIfCurrent(
                            generation,
                            "正在翻译字幕…");
                        var translationStarted = StartTranslation(
                            generation,
                            text,
                            cancellationToken);
                        if (translationStarted is not null)
                        {
                            await translationStarted.WaitAsync(
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                        when (iteration.IsCancellationRequested ||
                              cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                        PauseForPlacementFailure(
                            TranslationSubtitlePlacementStatus.MonitorMissing);
                    }
                    catch
                    {
                        ShowStatusIfCurrent(
                            generation,
                            "字幕采样失败，稍后重试。");
                    }
                    finally
                    {
                        lock (_sync)
                        {
                            if (ReferenceEquals(
                                _ocrIterationCancellation,
                                iteration))
                            {
                                _ocrIterationCancellation = null;
                            }
                        }
                        iteration.Dispose();
                    }
                }
                finally
                {
                    currentWork.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task? StartTranslation(
        long generation,
        string text,
        CancellationToken sessionToken)
    {
        CancellationTokenSource cancellation;
        CancellationTokenSource? abandonedCancellation;
        Task? abandonedTask;
        Task startedTask;
        long sequence;
        lock (_sync)
        {
            if (!IsCurrentUnpausedGenerationLocked(generation))
                return null;

            sequence = NextGeneration(_activeTranslationSequence);
            _activeTranslationSequence = sequence;
            _activeTranslationText = text;
            ReleaseTranslationStartedLocked();
            abandonedCancellation = _translationCancellation;
            abandonedTask = _translationTask;
            cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    sessionToken);
            _translationCancellation = cancellation;
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _translationStartedSignal = started;
            startedTask = started.Task;
            _translationTask = Task.Run(
                () => RunTranslationAsync(
                    generation,
                    sequence,
                    text,
                    cancellation),
                CancellationToken.None);
        }

        CancelQuietly(abandonedCancellation);
        ObserveAbandonedTask(abandonedTask);
        return startedTask;
    }

    private async Task RunTranslationAsync(
        long generation,
        long sequence,
        string text,
        CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            while (true)
            {
                Task<TranslationResponse> request;
                try
                {
                    request = _host.TranslateAsync(
                        text,
                        TranslationPurpose.Subtitle,
                        cancellationToken);
                }
                finally
                {
                    SignalTranslationStarted(sequence);
                }
                var response = await request.ConfigureAwait(false);
                if (!IsCurrentTranslation(
                        generation,
                        sequence,
                        cancellationToken))
                {
                    return;
                }

                if (response.FailureKind ==
                    TranslationFailureKind.Cancelled)
                {
                    return;
                }

                if (response.Success ||
                    response.FailureKind ==
                    TranslationFailureKind.NoTranslationNeeded)
                {
                    MarkTextTranslated(generation, sequence, text);
                    ShowResultIfCurrent(generation, response);
                    return;
                }

                if (response.FailureKind is
                    TranslationFailureKind.Authentication or
                    TranslationFailureKind.UnsupportedLanguage)
                {
                    Pause(
                        response.FailureKind ==
                        TranslationFailureKind.Authentication
                            ? "翻译服务认证失败，已暂停自动重试。"
                            : "当前语言组合不受支持，已暂停自动重试。");
                    return;
                }

                ShowErrorIfCurrent(
                    generation,
                    ResolveFailureMessage(response));
                ResetSignatureIfCurrent(generation);
                if (response.FailureKind is
                    TranslationFailureKind.Network or
                    TranslationFailureKind.Quota or
                    TranslationFailureKind.Timeout or
                    TranslationFailureKind.ProviderError)
                {
                    await _clock.DelayAsync(
                            FailureBackoff,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!IsCurrentTranslation(
                            generation,
                            sequence,
                            cancellationToken))
                    {
                        return;
                    }
                    continue;
                }

                MarkTranslationFailed(generation, sequence);
                return;
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (IsCurrentTranslation(
                    generation,
                    sequence,
                    cancellationToken))
            {
                ShowStatusIfCurrent(
                    generation,
                    "字幕翻译失败，稍后重试。");
                ResetSignatureIfCurrent(generation);
                MarkTranslationFailed(generation, sequence);
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(
                    _translationCancellation,
                    cancellation))
                {
                    _translationCancellation = null;
                    _translationTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task<CapturedFrameSignature?> CaptureSignatureAsync(
        TranslationCaptureRegion region,
        CancellationToken cancellationToken)
    {
        var captured = await _capture.CaptureRegionAsync(
                region,
                cancellationToken)
            .ConfigureAwait(false);
        if (captured is null)
            return null;

        try
        {
            return new CapturedFrameSignature(
                captured.Frame,
                SubtitleFrameChangeDetector.Create(captured.Frame));
        }
        catch
        {
            captured.Frame.Dispose();
            throw;
        }
    }

    private bool IsDuplicateText(string text)
    {
        lock (_sync)
        {
            if (_previousText is { } previous &&
                TranslationTextProcessor.IsNearDuplicate(
                    previous,
                    text))
            {
                return true;
            }

            return _activeTranslationText is { } active &&
                TranslationTextProcessor.IsNearDuplicate(active, text);
        }
    }

    private void MarkTextTranslated(
        long generation,
        long sequence,
        string text)
    {
        lock (_sync)
        {
            if (IsCurrentTranslationLocked(generation, sequence))
            {
                _previousText = text;
                _activeTranslationText = null;
            }
        }
    }

    private void MarkTranslationFailed(long generation, long sequence)
    {
        lock (_sync)
        {
            if (IsCurrentTranslationLocked(generation, sequence))
                _activeTranslationText = null;
        }
    }

    private void ResetSignatureIfCurrent(long generation)
    {
        lock (_sync)
        {
            if (IsCurrentGenerationLocked(generation))
                _previousSignature = null;
        }
    }

    private void SetPreviousSignature(
        long generation,
        FrameSignature signature)
    {
        lock (_sync)
        {
            if (IsCurrentGenerationLocked(generation))
                _previousSignature = signature;
        }
    }

    private void ShowStatusIfCurrent(
        long generation,
        string status)
    {
        if (GetCurrentOperation(generation) is { } operation)
            _host.ShowStatus(operation, status);
    }

    private void ShowResultIfCurrent(
        long generation,
        TranslationResponse response)
    {
        if (GetCurrentOperation(generation) is { } operation)
            _host.ShowResult(operation, response);
    }

    private void ShowErrorIfCurrent(
        long generation,
        string message)
    {
        if (GetCurrentOperation(generation) is { } operation)
            _host.ShowError(operation, message);
    }

    private TranslationRouteHandle? GetCurrentOperation(long generation)
    {
        lock (_sync)
        {
            return IsCurrentUnpausedGenerationLocked(generation)
                ? _operation
                : null;
        }
    }

    private TranslationSettings? GetCurrentSettings(long generation)
    {
        lock (_sync)
        {
            return IsCurrentGenerationLocked(generation)
                ? _settings
                : null;
        }
    }

    private bool IsCurrentWork(long generation, long sequence)
    {
        lock (_sync)
            return IsCurrentWorkLocked(generation, sequence);
    }

    private bool IsCurrentWorkLocked(long generation, long sequence) =>
        IsCurrentUnpausedGenerationLocked(generation) &&
        _latestPublishedSequence == sequence;

    private bool IsCurrentTranslation(
        long generation,
        long sequence,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            return !cancellationToken.IsCancellationRequested &&
                _activeTranslationSequence == sequence &&
                IsCurrentUnpausedGenerationLocked(generation);
        }
    }

    private bool IsCurrentTranslationLocked(
        long generation,
        long sequence) =>
        _activeTranslationSequence == sequence &&
        IsCurrentUnpausedGenerationLocked(generation);

    private bool IsCurrentGenerationLocked(long generation) =>
        _running &&
        !_disposed &&
        _generation == generation;

    private bool IsCurrentUnpausedGenerationLocked(long generation) =>
        IsCurrentGenerationLocked(generation) &&
        !_paused;

    private async Task WaitWhilePausedAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? wait;
            lock (_sync)
            {
                if (!_paused)
                    return;
                wait = _resumeSignal?.Task;
            }
            if (wait is null)
                return;
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ResumeWaiterLocked()
    {
        var waiter = _resumeSignal;
        _resumeSignal = null;
        waiter?.TrySetResult();
    }

    private void ReleaseFirstCandidateObservation()
    {
        lock (_sync)
            ReleaseFirstCandidateObservationLocked();
    }

    private void ReleaseFirstCandidateObservationLocked()
    {
        var signal = _firstCandidateObserved;
        _firstCandidateObserved = null;
        signal?.TrySetResult();
    }

    private void SignalTranslationStarted(long sequence)
    {
        TaskCompletionSource? signal = null;
        lock (_sync)
        {
            if (_activeTranslationSequence == sequence)
            {
                signal = _translationStartedSignal;
                _translationStartedSignal = null;
            }
        }
        signal?.TrySetResult();
    }

    private void ReleaseTranslationStartedLocked()
    {
        var signal = _translationStartedSignal;
        _translationStartedSignal = null;
        signal?.TrySetResult();
    }

    private void PauseForPlacementFailure(
        TranslationSubtitlePlacementStatus status) =>
        PauseCore(
            status == TranslationSubtitlePlacementStatus.MonitorMissing
                ? "字幕区域所在显示器不可用，请重新选择区域。"
                : "无法避开字幕捕获区域，请重新选择字幕区域。",
            placementFailure: true);

    private static TimeSpan GetCaptureDelay(TranslationSettings settings) =>
        TimeSpan.FromMilliseconds(
            Math.Clamp(
                settings.SubtitleRefreshIntervalMs,
                100,
                1500));

    private void CompleteSession(
        long generation,
        CancellationTokenSource session)
    {
        TranslationRouteHandle? dismissOperation = null;
        LatestSubtitleWorkSlot<SubtitleOcrWork>? work = null;
        CancellationTokenSource? ocrIteration;
        CancellationTokenSource? translationCancellation;
        Task? translationTask;
        lock (_sync)
        {
            if (_generation == generation)
            {
                _running = false;
                _paused = false;
                _placementFailurePaused = false;
                _captureIterationCancellation = null;
                ocrIteration = _ocrIterationCancellation;
                _ocrIterationCancellation = null;
                translationCancellation = _translationCancellation;
                _translationCancellation = null;
                translationTask = _translationTask;
                _translationTask = null;
                _previousSignature = null;
                _previousText = null;
                _activeTranslationText = null;
                ResumeWaiterLocked();
                ReleaseFirstCandidateObservationLocked();
                ReleaseTranslationStartedLocked();
                dismissOperation = _operation;
                _operation = null;
                _settings = null;
                work = _ocrWork;
                _ocrWork = null;
                if (ReferenceEquals(_sessionCancellation, session))
                    _sessionCancellation = null;
            }
            else
            {
                ocrIteration = null;
                translationCancellation = null;
                translationTask = null;
            }
        }

        CancelQuietly(ocrIteration);
        CancelQuietly(translationCancellation);
        ObserveAbandonedTask(translationTask);
        work?.Dispose();

        if (dismissOperation is { } current)
        {
            _host.SetSubtitleControls(
                current,
                active: false,
                paused: false,
                canReselect: false);
        }
    }

    private static void ObserveAbandonedTask(Task? task)
    {
        if (task is null)
            return;
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task AwaitTaskAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The task is owned by this controller; stop must remain best-effort.
        }
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string ResolveFailureMessage(
        TranslationResponse response) =>
        !string.IsNullOrWhiteSpace(response.Message)
            ? response.Message
            : response.FailureKind switch
            {
                TranslationFailureKind.Authentication =>
                    "翻译服务认证失败。",
                TranslationFailureKind.Quota =>
                    "翻译服务额度已用尽。",
                TranslationFailureKind.Network =>
                    "网络连接不可用。",
                TranslationFailureKind.Timeout =>
                    "翻译请求超时。",
                TranslationFailureKind.UnsupportedLanguage =>
                    "当前语言组合不受支持。",
                _ => "字幕翻译失败，稍后重试。"
            };

    private static long NextGeneration(long value)
    {
        var next = value + 1;
        return next <= 0 ? 1 : next;
    }

    private sealed record CapturedFrameSignature(
        CaptureFrame Frame,
        FrameSignature Signature) : IDisposable
    {
        public void Dispose() => Frame.Dispose();
    }

    private sealed class SubtitleOcrWork(
        long generation,
        long sequence,
        string? languageTag,
        CapturedFrameSignature captured) : IDisposable
    {
        private bool _disposed;

        public long Generation { get; } = generation;
        public long Sequence { get; } = sequence;
        public string? LanguageTag { get; } = languageTag;
        public CaptureFrame Frame => captured.Frame;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            captured.Dispose();
        }
    }
}
