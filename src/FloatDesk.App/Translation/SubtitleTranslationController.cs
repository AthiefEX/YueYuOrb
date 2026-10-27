using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class SubtitleTranslationController : IDisposable
{
    private static readonly TimeSpan StabilityDelay =
        TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FailureBackoff =
        TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private readonly IScreenOcrService _ocr;
    private readonly ITranslationSubtitleCapture _capture;
    private readonly ITranslationRouteHost _host;
    private readonly IClock _clock;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _iterationCancellation;
    private TaskCompletionSource? _resumeSignal;
    private Task? _loopTask;
    private TranslationRouteHandle? _operation;
    private TranslationSettings? _settings;
    private FrameSignature? _previousSignature;
    private string? _previousText;
    private long _generation;
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
            _paused = false;
            _placementFailurePaused = false;
            _resumeSignal = null;
            _running = true;
            _sessionCancellation = session;
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
        {
            PauseForPlacementFailure(placement);
        }
        _loopTask = Task.Run(
            () => RunLoopAsync(
                generation,
                settings,
                session),
            CancellationToken.None);
    }

    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? session;
        lock (_sync)
        {
            if (!_running && _loopTask is null)
                return;
            _running = false;
            _paused = false;
            _placementFailurePaused = false;
            loop = _loopTask;
            session = _sessionCancellation;
            _sessionCancellation = null;
            _loopTask = null;
            CancelIterationLocked();
            ResumeWaiterLocked();
        }

        CancelQuietly(session);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        session?.Dispose();
    }

    public void Pause(string reason) =>
        PauseCore(reason, placementFailure: false);

    private void PauseCore(string reason, bool placementFailure)
    {
        TranslationRouteHandle? operation;
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
                CancelIterationLocked();
            }
            operation = _operation;
        }

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
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _running = false;
            _paused = false;
            _placementFailurePaused = false;
            session = _sessionCancellation;
            _sessionCancellation = null;
            _loopTask = null;
            CancelIterationLocked();
            ResumeWaiterLocked();
        }
        CancelQuietly(session);
        session?.Dispose();
    }

    private async Task RunLoopAsync(
        long generation,
        TranslationSettings settings,
        CancellationTokenSource session)
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
                var currentSettings = GetCurrentSettings(
                        generation)
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
                    _iterationCancellation = iteration;
                }

                var retryWithBackoff = false;
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
                    using var stable = await CaptureSignatureAsync(
                            currentSettings.SubtitleRegion!,
                            iteration.Token)
                        .ConfigureAwait(false);
                    if (stable is null)
                        continue;
                    SetPreviousSignature(
                        generation,
                        stable.Signature);

                    var ocrResult = await _ocr.RecognizeAsync(
                            stable.Frame,
                            currentSettings.OcrLanguage,
                            iteration.Token)
                        .ConfigureAwait(false);
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
                    var response = await _host.TranslateAsync(
                            text,
                            TranslationPurpose.Subtitle,
                            iteration.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentUnpausedGeneration(generation) ||
                        cancellationToken.IsCancellationRequested)
                    {
                        continue;
                    }

                    if (response.FailureKind ==
                        TranslationFailureKind.Cancelled)
                    {
                        continue;
                    }
                    if (response.Success)
                    {
                        MarkTextTranslated(generation, text);
                        ShowResultIfCurrent(
                            generation,
                            response);
                        continue;
                    }
                    if (response.FailureKind ==
                        TranslationFailureKind.NoTranslationNeeded)
                    {
                        MarkTextTranslated(generation, text);
                        ShowResultIfCurrent(
                            generation,
                            response);
                        continue;
                    }

                    ShowErrorIfCurrent(
                        generation,
                        ResolveFailureMessage(response));
                    ResetSignatureIfCurrent(generation);
                    if (response.FailureKind is
                        TranslationFailureKind.Authentication or
                        TranslationFailureKind.UnsupportedLanguage)
                    {
                        Pause(
                            response.FailureKind ==
                            TranslationFailureKind.Authentication
                                ? "翻译服务认证失败，已暂停自动重试。"
                                : "当前语言组合不受支持，已暂停自动重试。");
                    }
                    else if (response.FailureKind is
                             TranslationFailureKind.Network or
                             TranslationFailureKind.Quota or
                             TranslationFailureKind.Timeout or
                             TranslationFailureKind.ProviderError)
                    {
                        retryWithBackoff = true;
                    }
                }
                catch (OperationCanceledException)
                    when (iteration.IsCancellationRequested)
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
                            _iterationCancellation,
                            iteration))
                        {
                            _iterationCancellation = null;
                        }
                    }
                    iteration.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                    break;
                var delay = retryWithBackoff
                    ? FailureBackoff
                    : TimeSpan.FromMilliseconds(
                        Math.Clamp(
                            currentSettings.SubtitleRefreshIntervalMs,
                            300,
                            1500));
                try
                {
                    await _clock.DelayAsync(delay, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            TranslationRouteHandle? dismissOperation = null;
            lock (_sync)
            {
                if (_generation == generation)
                {
                    _running = false;
                    _paused = false;
                    _placementFailurePaused = false;
                    _iterationCancellation = null;
                    _previousSignature = null;
                    _previousText = null;
                    ResumeWaiterLocked();
                    dismissOperation = _operation;
                    _operation = null;
                    _settings = null;
                    if (ReferenceEquals(_sessionCancellation, session))
                        _sessionCancellation = null;
                }
            }

            if (dismissOperation is { } current)
            {
                _host.SetSubtitleControls(
                    current,
                    active: false,
                    paused: false,
                    canReselect: false);
            }
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
            return _previousText is { } previous &&
                   TranslationTextProcessor.IsNearDuplicate(
                       previous,
                       text);
        }
    }

    private void MarkTextTranslated(
        long generation,
        string text)
    {
        lock (_sync)
        {
            if (IsCurrentGenerationLocked(generation))
                _previousText = text;
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

    private bool IsCurrentGeneration(long generation)
    {
        lock (_sync)
            return IsCurrentGenerationLocked(generation);
    }

    private bool IsCurrentUnpausedGeneration(long generation)
    {
        lock (_sync)
            return IsCurrentUnpausedGenerationLocked(generation);
    }

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

    private void PauseForPlacementFailure(
        TranslationSubtitlePlacementStatus status) =>
        PauseCore(
            status == TranslationSubtitlePlacementStatus.MonitorMissing
                ? "字幕区域所在显示器不可用，请重新选择区域。"
                : "无法避开字幕捕获区域，请重新选择字幕区域。",
            placementFailure: true);

    private void CancelIterationLocked()
    {
        var iteration = _iterationCancellation;
        _iterationCancellation = null;
        CancelQuietly(iteration);
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
}
