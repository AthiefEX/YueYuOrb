using System.Windows;
using System.Windows.Threading;
using FloatDesk.App.Coordination;
using FloatDesk.App.Translation;
using FloatDesk.App.ViewModels;
using FloatDesk.App.Views;
using FloatDesk.Core;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Input;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Diagnostics;
using FloatDesk.Infrastructure.Notes;
using FloatDesk.Infrastructure.Settings;
using FloatDesk.Infrastructure.Translation;
using FloatDesk.Windows.Foreground;
using FloatDesk.Windows.Input;
using FloatDesk.Windows.Launcher;
using FloatDesk.Windows.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App;

internal sealed class AppRuntime : IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly FileLogger _logger;
    private readonly SettingsCoordinator _settings;
    private readonly SqliteNoteRepository _notes;
    private readonly ForegroundMonitor _foreground;
    private readonly InputLockEngine _input;
    private readonly WindowsAppLauncher _launcher = new();
    private readonly DpapiTranslationCredentialStore _translationCredentials;
    private readonly TranslationProviderFactory _translationProviders = new();
    private readonly WindowsScreenOcrService _ocr = new();
    private readonly TranslationOverlayWindow _translationOverlay;
    private readonly SelectedTextReader _selectedTextReader;
    private readonly TranslationRegionCaptureService _translationRegionCapture;
    private readonly TranslationRuntime _translationRuntime;
    private readonly CaptureSessionGate _captureSessionGate = new();
    private readonly RegionSelectionCoordinator _regionSelection = new();
    private readonly DispatcherTimer _inputTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _foregroundCts;
    private WindowCoordinator? _coordinator;
    private CaptureCoordinator? _captureCoordinator;
    private OrbWindow? _orb;
    private QuickPanelWindow? _panel;
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private ForegroundSnapshot _current = EmptySnapshot();
    private ForegroundSnapshot? _lastExternal;
    private ForegroundSnapshot? _wheelTranslationTarget;
    private bool _manualEnglish;
    private bool _emergencyPaused;
    private bool _sessionLocked;
    private bool _userVisible = true;
    private bool _captureActive;
    private bool _inputPollInProgress;
    private bool _disposed;

    public AppRuntime(AppDataPaths paths)
    {
        _paths = paths;
        _paths.EnsureCreated();
        _logger = new FileLogger(paths.Logs);
        _settings = new SettingsCoordinator(new JsonSettingsStore(paths.Root));
        _notes = new SqliteNoteRepository(paths.NotesDatabase);
        _translationCredentials = new DpapiTranslationCredentialStore(
            Path.Combine(paths.Root, "translation-secrets.dat"));
        _translationOverlay = new TranslationOverlayWindow();
        _selectedTextReader = new SelectedTextReader();
        _translationRegionCapture = new TranslationRegionCaptureService(
            _captureSessionGate,
            () => _coordinator,
            _regionSelection,
            (eventName, properties) =>
                _ = _logger.WriteAsync(eventName, properties));
        _translationRuntime = new TranslationRuntime(
            _translationOverlay,
            _selectedTextReader,
            _translationProviders,
            _translationCredentials,
            () => _current,
            () => _wheelTranslationTarget ?? _lastExternal,
            ocr: _ocr,
            regionCapture: _translationRegionCapture,
            settingsCoordinator: _settings);
        _foreground = new ForegroundMonitor();
        _input = new InputLockEngine(
            new WindowsInputBackend(),
            new WindowsKeyboardSwitchGuard());
    }

    public async Task InitializeAsync()
    {
        await _logger.WriteAsync("startup.begin");
        await _settings.InitializeAsync(_lifetime.Token);
        await _notes.InitializeAsync(_lifetime.Token);
        _ = _notes.PurgeDeletedAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            _lifetime.Token);

        var factories = new Dictionary<ToolWindow, Func<Window>>
        {
            [ToolWindow.QuickPanel] = () => _panel!,
            [ToolWindow.Settings] = CreateSettingsWindow,
            [ToolWindow.AiBrowser] = CreateAiWindow,
            [ToolWindow.Notes] = CreateNotesWindow
        };
        _orb = new OrbWindow(
            OpenQuickPanel,
            OnOrbPositionChanged,
            ShowSettings,
            OpenAi,
            OpenNotes,
            () => _ = StartCaptureAsync(CaptureMode.Region),
            ToggleOrbVisibility,
            RequestExit,
            panelToggle: ToggleQuickPanel,
            panelOpenState: () => _panel?.IsPanelOpen == true);
        _panel = new QuickPanelWindow();
        _panel.LaunchRequested += OnLaunchRequested;
        _panel.SettingsRequested += ShowSettings;
        _panel.InputLockToggleRequested += ToggleManualEnglish;
        _panel.AiRequested += OpenAi;
        _panel.NotesRequested += OpenNotes;
        _panel.CaptureRequested += mode => _ = StartCaptureAsync(mode);
        _panel.TranslateSelectionRequested +=
            OnTranslateSelectionRequested;
        _panel.TranslateRegionRequested +=
            OnTranslateRegionRequested;
        _panel.TranslateSubtitlesRequested +=
            OnTranslateSubtitlesRequested;
        _translationOverlay.PauseResumeRequested +=
            _translationRuntime.ToggleSubtitlePause;
        _translationOverlay.ReselectRegionRequested +=
            OnReselectSubtitleRegionRequested;
        _translationOverlay.StopSubtitleRequested +=
            OnStopSubtitlesRequested;

        _coordinator = new WindowCoordinator(factories, _orb, _panel);
        _captureCoordinator = new CaptureCoordinator(
            _coordinator,
            _input,
            () => _settings.Current,
            () => _lastExternal?.TopLevelHwnd ?? 0,
            active =>
            {
                _captureActive = active;
                if (active)
                    _translationRuntime.PauseForCapture();
                else
                    _translationRuntime.ResumeAfterCapture();
                UpdateInputPollingCadence();
            },
            (eventName, properties) =>
                _ = _logger.WriteAsync(eventName, properties),
            sessionGate: _captureSessionGate,
            regionSelection: _regionSelection);
        _coordinator.WindowCreated += ApplyToolSettings;
        _settings.Changed += OnSettingsChanged;
        _input.StatusChanged += OnInputStatusChanged;
        _foreground.Changed += OnForegroundChanged;

        _tray = new TrayService(
            OpenQuickPanel,
            ShowSettings,
            OpenNotes,
            OpenAi,
            () => _ = StartCaptureAsync(CaptureMode.Region),
            ToggleEmergencyPause,
            RequestExit,
            ToggleOrbVisibility);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplySettings(_settings.Current);
            _orb.Show();
            ApplyOrbPlacement(_settings.Current, usePrimaryMonitor: true);
            _panel.Apply(
                _settings.Current,
                _manualEnglish,
                _input.Status.Reason);
        });

        _hotkeys = new HotkeyService(_settings.Current.Hotkeys);
        _hotkeys.Pressed += OnHotkeyPressed;
        if (_hotkeys.EffectiveInitialBindings.TryGetValue(
                HotkeyAction.RegionCapture,
                out var captureHotkey))
        {
            _panel.SetRegionCaptureHotkey(
                SettingsViewModel.FormatHotkey(captureHotkey));
        }
        foreach (var (action, effective) in _hotkeys.EffectiveInitialBindings)
        {
            if (_settings.Current.Hotkeys.TryGetValue(action, out var requested) &&
                requested != effective)
            {
                await _logger.WriteAsync(
                    "hotkey.initial.fallback",
                    new Dictionary<string, object?>
                    {
                        ["action"] = action.ToString(),
                        ["requested"] = SettingsViewModel.FormatHotkey(requested),
                        ["effective"] = SettingsViewModel.FormatHotkey(effective)
                    });
                _tray?.ShowWarning(
                    $"{ProductInfo.Name}快捷键已自动切换",
                    $"{SettingsViewModel.FormatHotkey(requested)} 已被其他程序占用，" +
                    $"现使用 {SettingsViewModel.FormatHotkey(effective)}。" +
                    "也可从悬浮球快捷面板或托盘菜单启动框选截图。");
            }
        }
        foreach (var action in _hotkeys.FailedInitialBindings)
        {
            await _logger.WriteAsync(
                "hotkey.initial.failed",
                new Dictionary<string, object?>
                {
                    ["action"] = action.ToString(),
                    ["errorCode"] = "HotkeyConflict"
                });
            _tray?.ShowWarning(
                $"{ProductInfo.Name}快捷键不可用",
                $"{action} 的快捷键被其他程序占用；仍可从悬浮球快捷面板或托盘菜单使用该功能。");
        }

        _foreground.Start();
        var initial = await _foreground.RefreshAsync(_lifetime.Token);
        await ProcessForegroundAsync(initial, _lifetime.Token);

        _inputTimer.Tick += async (_, _) => await PollInputAsync();
        _overlayTimer.Tick += (_, _) => ApplyOverlay(_foreground.Current);
        _inputTimer.Start();
        _overlayTimer.Start();

        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged +=
            OnUserPreferenceChanged;
        await _logger.WriteAsync("startup.complete");
    }

    public void ShowTool(ToolWindow tool)
    {
        if (tool == ToolWindow.QuickPanel)
        {
            OpenQuickPanel();
            return;
        }
        _coordinator?.ShowTool(tool);
        if (tool == ToolWindow.AiBrowser)
            _ = EnsureAiShownAsync();
        else if (tool == ToolWindow.Notes && GetTool<NotesWindow>() is { } notes)
            _ = notes.ShowAndActivateAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _lifetime.Cancel();
            _inputTimer.Stop();
            _overlayTimer.Stop();
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -=
                OnUserPreferenceChanged;
            _settings.Changed -= OnSettingsChanged;
            _input.StatusChanged -= OnInputStatusChanged;
            _foreground.Changed -= OnForegroundChanged;
            _translationOverlay.PauseResumeRequested -=
                _translationRuntime.ToggleSubtitlePause;
            _translationOverlay.ReselectRegionRequested -=
                OnReselectSubtitleRegionRequested;
            _translationOverlay.StopSubtitleRequested -=
                OnStopSubtitlesRequested;
            _translationRuntime.Dispose();
            _translationRegionCapture.Dispose();
            _selectedTextReader.Dispose();
            if (_translationOverlay.IsLoaded)
                _translationOverlay.Close();

            var notes = GetTool<NotesWindow>();
            if (notes is not null)
            {
                notes.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
                notes.Dispose();
            }
            var browser = GetTool<AiBrowserWindow>();
            if (browser is not null)
            {
                browser.DisposeBrowserAsync().AsTask().GetAwaiter().GetResult();
                browser.Dispose();
            }

            _captureCoordinator?.Dispose();
            var regionShutdown = ShutdownRegionSelectionForApplication(
                _regionSelection,
                System.Windows.Application.Current?.Dispatcher.CheckAccess() == true);
            if (!regionShutdown.Drained)
            {
                _logger.WriteAsync(
                    "capture.region.shutdown.undrained",
                    new Dictionary<string, object?>
                    {
                        ["pendingCleanupCount"] =
                            regionShutdown.PendingCleanupCount
                    }).GetAwaiter().GetResult();
            }
            _captureSessionGate.Dispose();
            _ocr.Dispose();
            _foreground.Dispose();
            _hotkeys?.Dispose();
            _hotkeys = null;
            _tray?.Dispose();
            _tray = null;
            _input.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _settings.Dispose();
            _translationProviders.Dispose();
            _translationCredentials.Dispose();
            _lifetime.Dispose();
            _logger.WriteAsync("shutdown.complete").GetAwaiter().GetResult();
            _logger.Dispose();
        }
        finally
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -=
                OnUserPreferenceChanged;
        }
    }

    internal static RegionSelectionShutdownResult
        ShutdownRegionSelectionForApplication(
            RegionSelectionCoordinator coordinator,
            bool isOwnerThread)
    {
        return isOwnerThread
            ? coordinator.ShutdownOnOwnerThread()
            : coordinator.ShutdownAsync().GetAwaiter().GetResult();
    }

    private SettingsWindow CreateSettingsWindow() =>
        new SettingsWindow(
            _settings,
            _paths,
            () => _current.IsOwnInteractive ? _lastExternal?.ExePath : _current.ExePath,
            _translationCredentials,
            _translationProviders,
            () => _ocr.AvailableLanguages);

    private AiBrowserWindow CreateAiWindow()
    {
        var window = new AiBrowserWindow(_paths, PersistSelectedSiteAsync);
        window.ApplySettings(_settings.Current);
        return window;
    }

    private NotesWindow CreateNotesWindow() => new NotesWindow(_notes);

    private void ApplyToolSettings(Window window)
    {
        if (window is AiBrowserWindow browser)
            browser.ApplySettings(_settings.Current);
    }

    private async Task EnsureAiShownAsync()
    {
        if (GetTool<AiBrowserWindow>() is { } browser)
        {
            browser.ApplySettings(_settings.Current);
            await browser.ShowAndEnsureAsync(_lifetime.Token);
        }
    }

    private async Task PersistSelectedSiteAsync(BrowserSite site)
    {
        var result = await _settings.UpdateAsync(
            current => current with { SelectedBrowserSiteId = site.Id },
            _lifetime.Token);
        if (!result.Success)
            await _logger.WriteAsync(
                "browser.selectSite.failed",
                new Dictionary<string, object?>
                {
                    ["errorCode"] = result.ErrorCode
                });
    }

    private void OpenQuickPanel()
    {
        if (_panel is null || _orb is null || !_userVisible)
            return;
        _panel.Apply(_settings.Current, _manualEnglish, _input.Status.Reason);
        _panel.ShowNear(_orb, GetPhysicalBounds(_orb));
    }

    private void ToggleQuickPanel(bool panelWasOpenAtPress)
    {
        if (_panel is null || _orb is null || !_userVisible)
            return;
        if (!panelWasOpenAtPress && !_panel.IsPanelOpen)
            _panel.Apply(_settings.Current, _manualEnglish, _input.Status.Reason);
        _panel.ToggleNear(
            _orb,
            GetPhysicalBounds(_orb),
            panelWasOpenAtPress);
    }

    private void ShowSettings() => ShowTool(ToolWindow.Settings);

    private void OpenAi() => ShowTool(ToolWindow.AiBrowser);

    private void OpenNotes() => ShowTool(ToolWindow.Notes);

    private void OnTranslateSelectionRequested()
    {
        var target = SelectTranslationTarget();
        _wheelTranslationTarget = target;
        _panel?.HideAnimated();
        _ = TranslateWheelSelectionAsync(target);
    }

    private async Task TranslateWheelSelectionAsync(
        ForegroundSnapshot target)
    {
        try
        {
            await TranslateSelectionAsync(target);
        }
        finally
        {
            if (_wheelTranslationTarget == target)
                _wheelTranslationTarget = null;
        }
    }

    private async Task TranslateSelectionAsync(
        ForegroundSnapshot target)
    {
        try
        {
            await _translationRuntime.TranslateSelectionAsync(
                target,
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnTranslateRegionRequested()
    {
        _panel?.HideAnimated();
        _ = TranslateRegionAsync();
    }

    private async Task TranslateRegionAsync()
    {
        try
        {
            await _translationRuntime.TranslateRegionAsync(
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnTranslateSubtitlesRequested()
    {
        _panel?.HideAnimated();
        _ = ToggleSubtitlesAsync();
    }

    private async Task ToggleSubtitlesAsync()
    {
        try
        {
            await _translationRuntime.ToggleSubtitlesAsync(
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnReselectSubtitleRegionRequested() =>
        _ = ReselectSubtitleRegionAsync();

    private async Task ReselectSubtitleRegionAsync()
    {
        try
        {
            await _translationRuntime.ReselectSubtitleRegionAsync(
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (
            _lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnStopSubtitlesRequested() =>
        _ = StopSubtitlesAsync();

    private async Task StopSubtitlesAsync()
    {
        try
        {
            await _translationRuntime.StopSubtitlesAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ForegroundSnapshot SelectTranslationTarget()
    {
        if (_current.TopLevelHwnd != 0 &&
            !_current.IsOwnInteractive)
        {
            return _current;
        }
        return _lastExternal ?? _current;
    }

    private async void OnLaunchRequested(LauncherItem item)
    {
        _panel?.HideAnimated();
        var result = await _launcher.LaunchAsync(item, _lifetime.Token);
        if (!result.Success)
        {
            MessageBox.Show(
                result.Message ?? "无法启动应用。",
                $"{ProductInfo.Name}快捷应用",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Panel:
                OpenQuickPanel();
                break;
            case HotkeyAction.RegionCapture:
                _ = StartCaptureAsync(CaptureMode.Region);
                break;
            case HotkeyAction.WindowCapture:
                _ = StartCaptureAsync(CaptureMode.ActiveWindow);
                break;
            case HotkeyAction.MonitorCapture:
                _ = StartCaptureAsync(CaptureMode.CurrentMonitor);
                break;
            case HotkeyAction.PauseInput:
                ToggleEmergencyPause();
                break;
            case HotkeyAction.AiBrowser:
                OpenAi();
                break;
            case HotkeyAction.Notes:
                OpenNotes();
                break;
            case HotkeyAction.TranslateSelection:
                _ = TranslateSelectionAsync(
                    SelectTranslationTarget());
                break;
            case HotkeyAction.TranslateRegion:
                _ = TranslateRegionAsync();
                break;
            case HotkeyAction.ToggleSubtitles:
                _ = ToggleSubtitlesAsync();
                break;
        }
    }

    private void ToggleManualEnglish()
    {
        _manualEnglish = !_manualEnglish;
        _ = _logger.WriteAsync(
            "input.manual.toggle",
            new Dictionary<string, object?>
            {
                ["enabled"] = _manualEnglish,
                ["foregroundHwnd"] = _current.TopLevelHwnd.ToInt64(),
                ["foregroundPid"] = _current.ProcessId,
                ["foregroundPath"] = _current.ExePath,
                ["foregroundOwn"] = _current.IsOwnInteractive,
                ["lastExternalHwnd"] = _lastExternal?.TopLevelHwnd.ToInt64(),
                ["lastExternalPid"] = _lastExternal?.ProcessId,
                ["lastExternalPath"] = _lastExternal?.ExePath
            });
        _ = ReEvaluateCurrentAsync();
    }

    private void ToggleEmergencyPause()
    {
        _emergencyPaused = !_emergencyPaused;
        _translationRuntime.SetEmergencyPaused(_emergencyPaused);
        _panel?.Apply(
            _settings.Current,
            _manualEnglish,
            _emergencyPaused ? "紧急暂停已开启" : _input.Status.Reason);
        _ = ReEvaluateCurrentAsync();
    }

    private void ToggleOrbVisibility()
    {
        _userVisible = !_userVisible;
        ApplyOverlay(_foreground.Current);
        if (!_userVisible)
            _panel?.HideAnimated();
    }

    private async Task ReEvaluateCurrentAsync()
    {
        try
        {
            var snapshot = await _foreground.RefreshAsync(_lifetime.Token);
            await ProcessForegroundAsync(snapshot, _lifetime.Token, debounce: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnForegroundChanged(ForegroundSnapshot snapshot) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => ScheduleForeground(snapshot));

    private void ScheduleForeground(ForegroundSnapshot snapshot)
    {
        _foregroundCts?.Cancel();
        _foregroundCts?.Dispose();
        _foregroundCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _foregroundCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(50, token);
                var current = await _foreground.RefreshAsync(token);
                await ProcessForegroundAsync(current, token, debounce: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                await _logger.WriteAsync(
                    "foreground.processing.failed",
                    new Dictionary<string, object?>
                    {
                        ["type"] = exception.GetType().Name
                    });
            }
        }, token);
    }

    private async Task ProcessForegroundAsync(
        ForegroundSnapshot snapshot,
        CancellationToken cancellationToken,
        bool debounce = true)
    {
        if (debounce)
            await Task.Delay(50, cancellationToken);
        var foregroundChanged =
            !ForegroundMonitor.HasSameIdentity(_current, snapshot);
        _current = snapshot;
        if (snapshot.TopLevelHwnd != 0 && !snapshot.IsOwnInteractive)
            _lastExternal = snapshot;

        var rules = _settings.Current.InputLock.Rules;
        var context = new InputContext(
            _sessionLocked,
            _emergencyPaused,
            _captureActive,
            _manualEnglish,
            _settings.Current.InputLock.AutoEnabled);
        if (foregroundChanged)
        {
            var decision = InputPolicy.Decide(snapshot, context, rules);
            _ = _logger.WriteAsync(
                "input.foreground.decision",
                new Dictionary<string, object?>
                {
                    ["epoch"] = snapshot.Epoch,
                    ["foregroundHwnd"] = snapshot.TopLevelHwnd.ToInt64(),
                    ["focusHwnd"] = snapshot.FocusHwnd.ToInt64(),
                    ["foregroundPid"] = snapshot.ProcessId,
                    ["threadId"] = snapshot.ThreadId,
                    ["foregroundPath"] = snapshot.ExePath,
                    ["foregroundOwn"] = snapshot.IsOwnInteractive,
                    ["manualEnabled"] = _manualEnglish,
                    ["autoEnabled"] = _settings.Current.InputLock.AutoEnabled,
                    ["action"] = decision.Action.ToString(),
                    ["reason"] = decision.Reason
                });
        }
        await _input.UpdateAsync(
            snapshot,
            context,
            rules,
            new InputLockOptions(_settings.Current.InputLock.TargetKlid),
            cancellationToken);
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () =>
            {
                if (foregroundChanged)
                    ApplyOverlay(snapshot);
                UpdateInputPollingCadence();
            });
    }

    private async Task PollInputAsync()
    {
        if (_inputPollInProgress)
            return;
        _inputPollInProgress = true;
        try
        {
            if (_disposed || _captureActive || _sessionLocked)
            {
                UpdateInputPollingCadence();
                return;
            }
            var rules = _settings.Current.InputLock.Rules;
            var decision = InputPolicy.Decide(
                _current,
                new InputContext(
                    _sessionLocked,
                    _emergencyPaused,
                    _captureActive,
                    _manualEnglish,
                    _settings.Current.InputLock.AutoEnabled),
                rules);
            if (decision.Action != InputAction.LockEnglish &&
                !_input.HasPendingRestore)
                return;
            var refreshed = await _foreground.RefreshAsync(_lifetime.Token);
            await ProcessForegroundAsync(refreshed, _lifetime.Token, debounce: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync(
                "input.poll.failed",
                new Dictionary<string, object?>
                {
                    ["type"] = exception.GetType().Name
                });
        }
        finally
        {
            _inputPollInProgress = false;
        }
    }

    private void OnInputStatusChanged(InputStatus status)
    {
        _ = _logger.WriteAsync(
            "input.status",
            new Dictionary<string, object?>
            {
                ["state"] = status.State.ToString(),
                ["epoch"] = status.Epoch,
                ["reason"] = status.Reason,
                ["active"] = _input.IsActive,
                ["pendingRestore"] = _input.HasPendingRestore
            });
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_panel is null)
                return;
            _panel.Apply(
                _settings.Current,
                _manualEnglish,
                _emergencyPaused ? "紧急暂停已开启" : status.Reason);
            UpdateInputPollingCadence();
        });
    }

    private void UpdateInputPollingCadence()
    {
        var interval = _input.IsActive && !_captureActive && !_sessionLocked
            ? TimeSpan.FromMilliseconds(50)
            : TimeSpan.FromMilliseconds(250);
        if (_inputTimer.Interval != interval)
            _inputTimer.Interval = interval;
    }

    private void ApplyOverlay(ForegroundSnapshot snapshot)
    {
        if (_orb is null || _disposed)
            return;
        var isFullscreen = snapshot.TopLevelHwnd != 0 &&
                           !snapshot.IsOwnInteractive &&
                           DisplayTopology.IsLikelyFullscreen(snapshot.TopLevelHwnd);
        var decision = OverlayPolicy.Evaluate(
            _userVisible,
            isFullscreen,
            _captureActive,
            _settings.Current.Overlay.AlwaysOnTop,
            _settings.Current.Overlay.FullscreenPolicy);
        _orb.ApplyOverlay(decision.Visible, decision.Topmost);
        if (!decision.Visible)
            _panel?.HideAnimated();
    }

    private void OnSettingsChanged(AppSettings settings) =>
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            ApplySettings(settings);
            ApplyOverlay(_foreground.Current);
        });

    private void ApplySettings(AppSettings settings)
    {
        ThemeService.Apply(settings.General.Theme);
        _orb?.ApplySettings(settings);
        KeepOrbOnCurrentMonitor();
        _translationRuntime.ApplySettings(settings);
        _panel?.Apply(
            settings,
            _manualEnglish,
            _emergencyPaused ? "紧急暂停已开启" : _input.Status.Reason);
        if (GetTool<AiBrowserWindow>() is { } browser)
            browser.ApplySettings(settings);
        if (_hotkeys is not null)
        {
            foreach (var (action, chord) in settings.Hotkeys)
            {
                var result = _hotkeys.Rebind(action, chord);
                if (!result.Success)
                {
                    MessageBox.Show(
                        $"{action} 快捷键未更新：{result.Message}。原绑定已保留。",
                        $"{ProductInfo.Name}快捷键",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    _ = _logger.WriteAsync(
                        "hotkey.rebind.failed",
                        new Dictionary<string, object?>
                        {
                            ["action"] = action.ToString(),
                            ["errorCode"] = result.ErrorCode
                        });
                }
            }
        }
    }

    private void OnOrbPositionChanged(PixelRect bounds)
    {
        if (_settings.Current.Orb.PositionLocked)
            return;
        var monitor = DisplayTopology.FromPoint(bounds.Location);
        if (monitor is null)
            return;
        var width = Math.Max(1, monitor.WorkArea.Width - bounds.Width);
        var height = Math.Max(1, monitor.WorkArea.Height - bounds.Height);
        var relativeX = Math.Clamp((bounds.Left - monitor.WorkArea.Left) / (double)width, 0, 1);
        var relativeY = Math.Clamp((bounds.Top - monitor.WorkArea.Top) / (double)height, 0, 1);
        _ = _settings.UpdateAsync(
            current => current with
            {
                Windows =
                [
                    new WindowPlacement(
                        "orb",
                        monitor.DeviceName,
                        relativeX,
                        relativeY,
                        bounds.Width,
                        bounds.Height)
                ]
            },
            _lifetime.Token);
    }

    private void ApplyOrbPlacement(
        AppSettings settings,
        bool usePrimaryMonitor = false)
    {
        if (_orb is null)
            return;
        var placement = settings.Windows.FirstOrDefault(item => item.Id == "orb");
        var monitors = DisplayTopology.GetMonitors();
        var monitor = usePrimaryMonitor
            ? DisplayTopology.SelectPrimaryMonitor(monitors)
            : monitors.FirstOrDefault(
                item => item.DeviceName == placement?.MonitorId)
                ?? DisplayTopology.SelectPrimaryMonitor(monitors);
        if (monitor is null)
            return;
        var bounds = GetPhysicalBounds(_orb);
        PixelPoint point;
        if (placement is null)
        {
            point = new PixelPoint(
                monitor.WorkArea.Right - bounds.Width - 12,
                monitor.WorkArea.Top + (monitor.WorkArea.Height - bounds.Height) / 2);
        }
        else
        {
            var x = monitor.WorkArea.Left +
                    (int)Math.Round(placement.RelativeX * Math.Max(0, monitor.WorkArea.Width - bounds.Width));
            var y = monitor.WorkArea.Top +
                    (int)Math.Round(placement.RelativeY * Math.Max(0, monitor.WorkArea.Height - bounds.Height));
            point = new PixelPoint(x, y);
        }
        var clamped = OrbPlacement.Clamp(
            new PixelRect(point.X, point.Y, bounds.Width, bounds.Height),
            monitor.WorkArea);
        _orb.SetPosition(clamped);
    }

    private void KeepOrbOnCurrentMonitor()
    {
        if (_orb is null || !_orb.IsVisible)
            return;
        var bounds = GetPhysicalBounds(_orb);
        var monitor = DisplayTopology.FromPoint(
            new PixelPoint(
                bounds.Left + (bounds.Width / 2),
                bounds.Top + (bounds.Height / 2)));
        if (monitor is null)
            return;
        var clamped = OrbPlacement.Clamp(bounds, monitor.WorkArea);
        if (clamped != bounds)
            _orb.SetPosition(clamped);
    }

    private void OnSessionSwitch(
        object sender,
        Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is
            Microsoft.Win32.SessionSwitchReason.SessionLock or
            Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect;
        _translationRuntime.SetSessionLocked(_sessionLocked);
        UpdateInputPollingCadence();
        if (!_sessionLocked)
            _ = ReEvaluateCurrentAsync();
    }

    private void OnPowerModeChanged(
        object sender,
        Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(
                async () =>
                {
                    var snapshot = await _foreground.RefreshAsync(_lifetime.Token);
                    await ProcessForegroundAsync(snapshot, _lifetime.Token);
                });
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_orb is null)
            return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _orb.ApplySettings(_settings.Current);
            ApplyOrbPlacement(_settings.Current);
            ApplyOverlay(_foreground.Current);
        });
    }

    private void OnUserPreferenceChanged(
        object sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_disposed)
            return;
        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (_disposed)
                return;
            ThemeService.Apply(_settings.Current.General.Theme);
            if (GetTool<AiBrowserWindow>() is { } browser)
                browser.ApplySettings(_settings.Current);
        });
    }

    private async void RequestExit()
    {
        var notes = GetTool<NotesWindow>();
        if (notes?.HasUnsavedChanges == true)
        {
            await notes.FlushAsync(_lifetime.Token);
            if (notes.HasUnsavedChanges)
            {
                MessageBox.Show(
                    "笔记保存失败，内容仍保留在记事本窗口。请重试、另存为文本或明确取消退出。",
                    "退出已取消",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                await notes.ShowAndActivateAsync();
                return;
            }
        }
        System.Windows.Application.Current.Shutdown();
    }

    private T? GetTool<T>() where T : Window =>
        _coordinator?.ToolWindows.OfType<T>().FirstOrDefault();

    private static PixelRect GetPhysicalBounds(Window window)
    {
        var native = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (native != 0 &&
            GetWindowRect(native, out var rect))
        {
            return new PixelRect(
                rect.Left,
                rect.Top,
                Math.Max(0, rect.Right - rect.Left),
                Math.Max(0, rect.Bottom - rect.Top));
        }
        return new PixelRect(
            (int)Math.Round(window.Left),
            (int)Math.Round(window.Top),
            Math.Max(1, (int)Math.Round(window.ActualWidth)),
            Math.Max(1, (int)Math.Round(window.ActualHeight)));
    }

    private async Task StartCaptureAsync(CaptureMode mode)
    {
        if (_captureCoordinator is null)
            return;
        _translationRuntime.PauseForCapture();
        try
        {
            _panel?.PrepareForScreenCapture();
            var result = await _captureCoordinator.StartAsync(
                mode,
                _lifetime.Token);
            if (!result.Success &&
                result.ErrorCode is not ("CaptureCancelled" or "CaptureBusy"))
            {
                MessageBox.Show(
                    result.Message ?? "截图失败。",
                    $"{ProductInfo.Name}截图",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            ApplyOverlay(_foreground.Current);
        }
        finally
        {
            _translationRuntime.ResumeAfterCapture();
        }
    }

    private static ForegroundSnapshot EmptySnapshot() =>
        new(0, 0, 0, 0, 0, null, null, false);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint hwnd,
        out WindowRect rect);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
