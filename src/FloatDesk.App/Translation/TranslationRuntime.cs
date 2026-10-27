using System.Security.Cryptography;
using System.Text;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class TranslationRuntime : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ITranslationRouteHost _host;
    private readonly SelectedTextTranslationController _controller;
    private readonly ITranslationProviderFactory _providers;
    private readonly ITranslationCredentialStore _credentials;
    private readonly Func<ForegroundSnapshot> _currentForeground;
    private readonly Func<ForegroundSnapshot?> _lastExternal;
    private readonly OcrTranslationController? _regionController;
    private readonly SubtitleTranslationController? _subtitleController;
    private readonly ITranslationRegionCapture? _subtitleCapture;
    private readonly IClock _clock;
    private readonly ISettingsCoordinator? _settingsCoordinator;
    private readonly SemaphoreSlim _subtitleGate = new(1, 1);
    private ConfigurationState? _configuration;
    private CancellationTokenSource? _interactiveCancellation;
    private AppSettings _latestSettings = AppSettings.CreateDefault();
    private volatile bool _subtitlePauseOwnedByCapture;
    private long _configurationGeneration;
    private bool _sessionLocked;
    private bool _emergencyPaused;
    private int _capturePauseDepth;
    private bool _disposed;

    public TranslationRuntime(
        TranslationOverlayWindow overlay,
        ISelectedTextReader reader,
        ITranslationProviderFactory providers,
        ITranslationCredentialStore credentials,
        Func<ForegroundSnapshot> currentForeground,
        Func<ForegroundSnapshot?> lastExternal,
        IClock? clock = null,
        IScreenOcrService? ocr = null,
        ITranslationRegionCapture? regionCapture = null,
        ISettingsCoordinator? settingsCoordinator = null)
        : this(
            new TranslationRouteHost(overlay),
            reader,
            providers,
            credentials,
            currentForeground,
            lastExternal,
            clock,
            ocr,
            regionCapture,
            settingsCoordinator)
    {
    }

    internal TranslationRuntime(
        ITranslationRouteHost host,
        ISelectedTextReader reader,
        ITranslationProviderFactory providers,
        ITranslationCredentialStore credentials,
        Func<ForegroundSnapshot> currentForeground,
        Func<ForegroundSnapshot?> lastExternal,
        IClock? clock = null,
        IScreenOcrService? ocr = null,
        ITranslationRegionCapture? regionCapture = null,
        ISettingsCoordinator? settingsCoordinator = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _providers = providers ??
            throw new ArgumentNullException(nameof(providers));
        _credentials = credentials ??
            throw new ArgumentNullException(nameof(credentials));
        _currentForeground = currentForeground ??
            throw new ArgumentNullException(nameof(currentForeground));
        _lastExternal = lastExternal ??
            throw new ArgumentNullException(nameof(lastExternal));
        _clock = clock ?? new SystemClock();
        _settingsCoordinator = settingsCoordinator;
        _controller = new SelectedTextTranslationController(reader, host);
        if (ocr is not null && regionCapture is not null)
        {
            _regionController = new OcrTranslationController(
                ocr,
                regionCapture,
                host,
                CurrentOcrLanguage);
        }
        if (ocr is not null && regionCapture is not null)
        {
            _subtitleCapture = regionCapture;
            _subtitleController = new SubtitleTranslationController(
                ocr,
                regionCapture,
                host,
                _clock);
        }
        if (host is TranslationRouteHost routeHost)
            routeHost.SetTranslator(TranslateTextAsync);
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var rebuild = false;
        var cancelledInteractive = false;
        lock (_sync)
        {
            if (_disposed)
                return;

            _latestSettings = settings;
            var fingerprint = ProviderFingerprint.From(settings);
            rebuild = _configuration is null ||
                      _configuration.ProviderFingerprint != fingerprint;
            if (rebuild)
            {
                cancelledInteractive = CancelInteractiveLocked();
                BeginConfigurationLocked(
                    settings.Translation,
                    fingerprint,
                    credential: null,
                    cancelInteractive: false);
            }
        }

        _subtitleController?.UpdateSettings(settings.Translation);
        if (rebuild)
            _ = StopSubtitlesAsync();
        if (cancelledInteractive)
            _host.DismissActiveOperation();
        _host.ApplySettings(settings);
    }

    public async Task TranslateSelectionAsync(
        ForegroundSnapshot fallbackTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fallbackTarget);
        if (IsPausedOrDisposed())
            return;

        var target = ResolveTarget(fallbackTarget);
        if (target.TopLevelHwnd == 0 || target.IsOwnInteractive)
        {
            ShowRouteError("未检测到可翻译的前台应用。");
            return;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            if (_disposed || IsPausedLocked())
            {
                operation.Dispose();
                return;
            }

            previous = _interactiveCancellation;
            _interactiveCancellation = operation;
        }
        previous?.Cancel();

        try
        {
            await _controller.RunAsync(target, operation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_interactiveCancellation, operation))
                    _interactiveCancellation = null;
            }
            operation.Dispose();
        }
    }

    public async Task TranslateRegionAsync(
        CancellationToken cancellationToken)
    {
        if (IsPausedOrDisposed())
            return;
        var controller = _regionController;
        if (controller is null)
        {
            ShowRouteError("区域 OCR 翻译尚未初始化。");
            return;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        CancellationTokenSource? previous;
        lock (_sync)
        {
            if (_disposed || IsPausedLocked())
            {
                operation.Dispose();
                return;
            }

            previous = _interactiveCancellation;
            _interactiveCancellation = operation;
        }
        previous?.Cancel();

        try
        {
            await controller.RunAsync(
                TranslationRoutePoint.GetCursorPosition(),
                operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_interactiveCancellation, operation))
                    _interactiveCancellation = null;
            }
            operation.Dispose();
        }
    }

    public async Task ToggleSubtitlesAsync(
        CancellationToken cancellationToken)
    {
        if (IsPausedOrDisposed())
            return;
        var controller = _subtitleController;
        if (controller is null)
        {
            ShowRouteError("字幕翻译尚未初始化。");
            return;
        }

        await _subtitleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (controller.IsRunning)
            {
                await controller.StopAsync().ConfigureAwait(false);
                return;
            }

            var settings = GetLatestSettings();
            if (settings.Translation.SubtitleRegion is null)
            {
                settings = await SelectAndPersistSubtitleRegionAsync(
                        showStatus: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (settings is null)
                    return;
            }

            using var operation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
            await controller.StartAsync(
                    settings.Translation,
                    operation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ShowRouteError("无法启动字幕翻译，请稍后重试。");
        }
        finally
        {
            _subtitleGate.Release();
        }
    }

    public Task StopSubtitlesAsync() =>
        _subtitleController?.StopAsync() ?? Task.CompletedTask;

    public void ToggleSubtitlePause()
    {
        var controller = _subtitleController;
        if (controller is not { IsRunning: true })
            return;
        if (controller.IsPaused)
            controller.Resume();
        else
            controller.Pause("字幕翻译已暂停。");
    }

    public async Task ReselectSubtitleRegionAsync(
        CancellationToken cancellationToken)
    {
        var controller = _subtitleController;
        if (controller is not { IsRunning: true })
            return;

        await _subtitleGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            controller.Pause("正在重新选择字幕区域。");
            var settings = await SelectAndPersistSubtitleRegionAsync(
                    showStatus: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (settings is null)
            {
                controller.Resume();
                return;
            }

            using var operation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
            await controller.ReselectAsync(
                    settings.Translation,
                    operation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            controller.Resume();
        }
        catch
        {
            ShowRouteError("无法重新选择字幕区域，请稍后重试。");
            controller.Resume();
        }
        finally
        {
            _subtitleGate.Release();
        }
    }

    public void SetSessionLocked(bool locked)
    {
        var changed = false;
        TranslationCenter? center = null;
        lock (_sync)
        {
            if (_disposed || _sessionLocked == locked)
                return;
            _sessionLocked = locked;
            changed = true;
            if (locked)
            {
                CancelInteractiveLocked();
                center = _configuration?.Center;
            }
        }
        if (changed && locked)
        {
            center?.ClearCache();
            _ = StopSubtitlesAsync();
            _host.DismissActiveOperation();
        }
    }

    public void SetEmergencyPaused(bool paused)
    {
        var changed = false;
        lock (_sync)
        {
            if (_disposed || _emergencyPaused == paused)
                return;
            _emergencyPaused = paused;
            changed = true;
            if (paused)
                CancelInteractiveLocked();
        }
        if (changed && paused)
        {
            _ = StopSubtitlesAsync();
            _host.DismissActiveOperation();
        }
    }

    public void PauseForCapture()
    {
        var changed = false;
        var cancelledInteractive = false;
        lock (_sync)
        {
            if (_disposed)
                return;
            if (_capturePauseDepth++ == 0)
            {
                changed = true;
                cancelledInteractive = CancelInteractiveLocked();
            }
        }
        if (changed)
        {
            if (_subtitleController is
                { IsRunning: true, IsPaused: false } subtitles)
            {
                _subtitlePauseOwnedByCapture = true;
                subtitles.Pause("截图进行中，字幕翻译已暂停。");
            }
            if (cancelledInteractive)
                _host.DismissActiveOperation();
        }
    }

    public void ResumeAfterCapture()
    {
        var finalResume = false;
        lock (_sync)
        {
            if (!_disposed && _capturePauseDepth > 0)
            {
                _capturePauseDepth--;
                finalResume = _capturePauseDepth == 0;
            }
        }
        if (finalResume && _subtitlePauseOwnedByCapture)
        {
            _subtitlePauseOwnedByCapture = false;
            _subtitleController?.Resume();
        }
    }

    public void Dispose()
    {
        ConfigurationState? configuration;
        CancellationTokenSource? interactive;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            configuration = _configuration;
            interactive = _interactiveCancellation;
            _interactiveCancellation = null;
        }

        _lifetime.Cancel();
        interactive?.Cancel();
        _subtitleController?.Dispose();
        if (configuration is not null)
        {
            configuration.Cancellation.Cancel();
            configuration.Center?.Dispose();
            ObserveConfigurationDisposal(configuration);
        }
        _host.DismissActiveOperation();
        _lifetime.Dispose();
    }

    internal async Task<TranslationResponse> TranslateTextAsync(
        string text,
        TranslationPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var configuration =
                await EnsureConfigurationAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (configuration.ErrorMessage is { } error)
            {
                return TranslationResponse.Fail(
                    text,
                    configuration.Settings.SourceLanguage,
                    ProviderName(configuration.Settings.Provider),
                    TranslationFailureKind.InvalidRequest,
                    "TranslationConfiguration",
                    error);
            }

            var center = configuration.Center;
            if (center is null)
            {
                return TranslationResponse.Fail(
                    text,
                    configuration.Settings.SourceLanguage,
                    ProviderName(configuration.Settings.Provider),
                    TranslationFailureKind.ProviderError,
                    "TranslationConfiguration",
                    "翻译服务尚未准备完成，请稍后重试。");
            }

            return await center.TranslateAsync(
                    text,
                    configuration.Settings.SourceLanguage,
                    configuration.Settings.TargetLanguage,
                    purpose,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return TranslationResponse.Fail(
                text,
                "",
                "翻译服务",
                TranslationFailureKind.Cancelled,
                "Cancelled",
                "Cancelled");
        }
        catch
        {
            return TranslationResponse.Fail(
                text,
                "",
                "翻译服务",
                TranslationFailureKind.ProviderError,
                "TranslationRuntimeFailed",
                "翻译失败，请稍后重试。");
        }
    }

    private async Task<ConfigurationState> EnsureConfigurationAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ConfigurationState current;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                current = _configuration ??
                    throw new InvalidOperationException(
                        "Translation settings are not initialized.");
            }

            await current.Ready.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            CredentialRead credential;
            try
            {
                credential = await ReadCredentialAsync(
                        current.Settings,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!ReferenceEquals(_configuration, current))
                    continue;
                if (current.CredentialStateKey == credential.StateKey)
                    return current;

                BeginConfigurationLocked(
                    current.Settings,
                    current.ProviderFingerprint,
                    credential,
                    cancelInteractive: false);
            }
        }
    }

    private void BeginConfigurationLocked(
        TranslationSettings settings,
        ProviderFingerprint fingerprint,
        CredentialRead? credential,
        bool cancelInteractive)
    {
        if (cancelInteractive)
            CancelInteractiveLocked();

        var previous = _configuration;
        if (previous is not null)
        {
            previous.Cancellation.Cancel();
            previous.Center?.Dispose();
            ObserveConfigurationDisposal(previous);
        }

        var generation = _configurationGeneration + 1;
        if (generation <= 0)
            generation = 1;
        _configurationGeneration = generation;
        var state = new ConfigurationState(
            generation,
            settings,
            fingerprint,
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token));
        _configuration = state;
        state.Ready = ConfigureAsync(state, credential);
    }

    private async Task ConfigureAsync(
        ConfigurationState state,
        CredentialRead? credential)
    {
        try
        {
            credential ??= await ReadCredentialAsync(
                    state.Settings,
                    state.Cancellation.Token)
                .ConfigureAwait(false);
            if (state.Cancellation.IsCancellationRequested)
                return;

            lock (_sync)
            {
                if (!ReferenceEquals(_configuration, state))
                    return;
                state.CredentialStateKey = credential.StateKey;
            }

            if (credential.ErrorMessage is { } credentialError)
            {
                SetConfigurationError(state, credentialError);
                return;
            }
            if (RequiresKey(state.Settings.Provider) &&
                string.IsNullOrWhiteSpace(credential.Key))
            {
                SetConfigurationError(
                    state,
                    "请先在“设置 > 实时翻译”中为当前翻译服务保存 API 密钥。");
                return;
            }

            ITranslationProvider provider;
            try
            {
                provider = _providers.Create(
                    state.Settings,
                    credential.Key);
            }
            catch (TranslationProviderConfigurationException)
            {
                SetConfigurationError(
                    state,
                    "翻译服务配置无效，请检查接口地址、模型和区域。");
                return;
            }
            catch
            {
                SetConfigurationError(
                    state,
                    "翻译服务配置无效，请检查后重试。");
                return;
            }

            if (state.Cancellation.IsCancellationRequested)
            {
                (provider as IDisposable)?.Dispose();
                return;
            }

            var center = new TranslationCenter(provider, _clock);
            lock (_sync)
            {
                if (!ReferenceEquals(_configuration, state) ||
                    state.Cancellation.IsCancellationRequested)
                {
                    center.Dispose();
                    return;
                }
                state.Center = center;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            SetConfigurationError(
                state,
                "无法准备翻译服务，请稍后重试。");
        }
    }

    private void SetConfigurationError(
        ConfigurationState state,
        string message)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_configuration, state))
                state.ErrorMessage = message;
        }
    }

    private async Task<CredentialRead> ReadCredentialAsync(
        TranslationSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Provider == TranslationProviderKind.MyMemory)
            return new CredentialRead("not-required", null, null);

        try
        {
            var key = await _credentials.GetAsync(
                    settings.Provider,
                    settings.Endpoint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
                return new CredentialRead("none", null, null);
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            return new CredentialRead(
                "sha256:" + fingerprint,
                key,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new CredentialRead(
                "unavailable",
                null,
                "无法读取翻译服务密钥，请检查系统凭据保护后重试。");
        }
    }

    private ForegroundSnapshot ResolveTarget(
        ForegroundSnapshot fallbackTarget)
    {
        try
        {
            var current = _currentForeground();
            if (current.TopLevelHwnd != 0 &&
                !current.IsOwnInteractive)
            {
                return current;
            }
        }
        catch
        {
        }

        try
        {
            var lastExternal = _lastExternal();
            if (lastExternal is
                {
                    TopLevelHwnd: not 0,
                    IsOwnInteractive: false
                })
            {
                return lastExternal;
            }
        }
        catch
        {
        }

        return fallbackTarget;
    }

    private string? CurrentOcrLanguage()
    {
        lock (_sync)
            return _latestSettings.Translation.OcrLanguage;
    }

    private AppSettings GetLatestSettings()
    {
        lock (_sync)
            return _latestSettings;
    }

    private async Task<AppSettings?> SelectAndPersistSubtitleRegionAsync(
        bool showStatus,
        CancellationToken cancellationToken)
    {
        var capture = _subtitleCapture;
        if (capture is null)
        {
            ShowRouteError("字幕区域选择尚未初始化。");
            return null;
        }

        CapturedTranslationRegion? selected;
        try
        {
            using var status = showStatus
                ? new SubtitleSelectionStatusScope(
                    _host,
                    "请选择字幕区域。")
                : null;
            selected = await capture.CaptureSelectedAsync(
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            ShowRouteError("无法截取字幕区域，请稍后重试。");
            return null;
        }

        if (selected is null)
            return null;
        using var frame = selected.Frame;
        if (selected.Region is not { } region)
        {
            ShowRouteError("无法确定字幕区域所在显示器，请重新选择。");
            return null;
        }

        return await PersistSubtitleRegionAsync(region, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AppSettings?> PersistSubtitleRegionAsync(
        TranslationCaptureRegion region,
        CancellationToken cancellationToken)
    {
        if (_settingsCoordinator is null)
        {
            var current = GetLatestSettings();
            var updated = WithSubtitleRegion(current, region);
            ApplySettings(updated);
            return updated;
        }

        AppSettings? updatedSettings = null;
        OperationResult result;
        try
        {
            result = await _settingsCoordinator.UpdateAsync(
                    current =>
                    {
                        updatedSettings =
                            WithSubtitleRegion(current, region);
                        return updatedSettings;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            ShowRouteError("无法保存字幕区域，请稍后重试。");
            return null;
        }

        if (!result.Success || updatedSettings is null)
        {
            ShowRouteError(
                result.Message ?? "无法保存字幕区域，请稍后重试。");
            return null;
        }

        ApplySettings(updatedSettings);
        return updatedSettings;
    }

    private static AppSettings WithSubtitleRegion(
        AppSettings settings,
        TranslationCaptureRegion region)
    {
        var current = settings.Translation;
        var placement = current.SubtitleOverlayPlacement;
        if (placement is null ||
            !string.Equals(
                placement.MonitorId,
                region.MonitorId,
                StringComparison.OrdinalIgnoreCase))
        {
            placement = new TranslationOverlayPlacement(
                region.MonitorId,
                0.5,
                0.86);
        }

        return settings with
        {
            Translation = current with
            {
                SubtitleRegion = region,
                SubtitleOverlayPlacement = placement
            }
        };
    }

    private void ShowRouteError(string message)
    {
        var operation = _host.ShowStatus(
            TranslationRoutePoint.GetCursorPosition(),
            message);
        _host.ShowError(operation, message);
    }

    private bool CancelInteractiveLocked()
    {
        var operation = _interactiveCancellation;
        _interactiveCancellation = null;
        if (operation is null)
            return false;
        operation.Cancel();
        return true;
    }

    private bool IsPausedOrDisposed()
    {
        lock (_sync)
            return _disposed || IsPausedLocked();
    }

    private bool IsPausedLocked() =>
        _sessionLocked ||
        _emergencyPaused ||
        _capturePauseDepth > 0;

    private static bool RequiresKey(
        TranslationProviderKind provider) =>
        provider is TranslationProviderKind.AzureTranslator or
            TranslationProviderKind.DeepL or
            TranslationProviderKind.OpenAiCompatible;

    private static string ProviderName(
        TranslationProviderKind provider) =>
        provider switch
        {
            TranslationProviderKind.MyMemory => "MyMemory",
            TranslationProviderKind.AzureTranslator => "Azure Translator",
            TranslationProviderKind.DeepL => "DeepL",
            TranslationProviderKind.LibreTranslate => "LibreTranslate",
            TranslationProviderKind.OpenAiCompatible => "OpenAI-compatible",
            _ => "翻译服务"
        };

    private sealed class SubtitleSelectionStatusScope : IDisposable
    {
        private readonly ITranslationRouteHost _host;

        internal SubtitleSelectionStatusScope(
            ITranslationRouteHost host,
            string status)
        {
            _host = host;
            _host.ShowStatus(
                TranslationRoutePoint.GetCursorPosition(),
                status);
        }

        public void Dispose() => _host.DismissActiveOperation();
    }

    private static void ObserveConfigurationDisposal(
        ConfigurationState configuration) =>
        _ = DisposeConfigurationAsync(configuration);

    private static async Task DisposeConfigurationAsync(
        ConfigurationState configuration)
    {
        try
        {
            await configuration.Ready.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            configuration.Center?.Dispose();
            configuration.Cancellation.Dispose();
        }
    }

    private sealed class ConfigurationState(
        long generation,
        TranslationSettings settings,
        ProviderFingerprint providerFingerprint,
        CancellationTokenSource cancellation)
    {
        internal long Generation { get; } = generation;
        internal TranslationSettings Settings { get; } = settings;
        internal ProviderFingerprint ProviderFingerprint { get; } =
            providerFingerprint;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        internal Task Ready { get; set; } = Task.CompletedTask;
        internal TranslationCenter? Center { get; set; }
        internal string CredentialStateKey { get; set; } = "";
        internal string? ErrorMessage { get; set; }
    }

    private sealed record CredentialRead(
        string StateKey,
        string? Key,
        string? ErrorMessage);

    private sealed record ProviderFingerprint(
        TranslationProviderKind Provider,
        string SourceLanguage,
        string TargetLanguage,
        string Endpoint,
        string Model,
        string AzureRegion,
        string? OcrLanguage)
    {
        internal static ProviderFingerprint From(AppSettings settings)
        {
            var translation = settings.Translation;
            return new ProviderFingerprint(
                translation.Provider,
                translation.SourceLanguage,
                translation.TargetLanguage,
                translation.Endpoint,
                translation.Model,
                translation.AzureRegion,
                translation.OcrLanguage);
        }
    }
}
