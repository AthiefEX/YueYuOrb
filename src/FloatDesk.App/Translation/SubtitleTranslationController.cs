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
        _loopTask = Task.Run(
            () => RunLoopAsync(
                generation,
                settings,
                operation,
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

    public void Pause(string reason)
    {
        TranslationRouteHandle? operation;
        lock (_sync)
        {
            if (!_running || _paused)
                return;
            _paused = true;
            _resumeSignal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _previousSignature = null;
            CancelIterationLocked();
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
        TranslationRouteHandle? operation;
        lock (_sync)
        {
            if (!_running || !_paused)
                return;
            _paused = false;
            ResumeWaiterLocked();
            operation = _operation;
        }

        if (operation is { } current)
        {
            _host.SetSubtitleControls(
                current,
                active: true,
                paused: false,
                canReselect: true);
            _host.ShowStatus(current, "字幕翻译已继续。");
        }
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
        TranslationRouteHandle operation,
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
                                operation,
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
                            operation,
                            "未识别到字幕文字。");
                        continue;
                    }
                    if (IsDuplicateText(text))
                        continue;

                    ShowStatusIfCurrent(
                        generation,
                        operation,
                        "正在翻译字幕…");
                    var response = await _host.TranslateAsync(
                            text,
                            TranslationPurpose.Subtitle,
                            iteration.Token)
                        .ConfigureAwait(false);
                    if (!IsCurrentGeneration(generation) ||
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
                        _host.ShowResult(operation, response);
                        continue;
                    }
                    if (response.FailureKind ==
                        TranslationFailureKind.NoTranslationNeeded)
                    {
                        MarkTextTranslated(generation, text);
                        _host.ShowResult(operation, response);
                        continue;
                    }

                    _host.ShowError(
                        operation,
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
                    Pause("字幕区域所在显示器不可用，请重新选择区域。");
                }
                catch
                {
                    ShowStatusIfCurrent(
                        generation,
                        operation,
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
        TranslationRouteHandle operation,
        string status)
    {
        if (IsCurrentGeneration(generation))
            _host.ShowStatus(operation, status);
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

    private bool IsCurrentGenerationLocked(long generation) =>
        _running &&
        !_disposed &&
        _generation == generation;

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
