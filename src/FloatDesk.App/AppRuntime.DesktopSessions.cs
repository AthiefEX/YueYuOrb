using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private System.Windows.Controls.Primitives.Popup? _desktopSessionPicker;
    private readonly HyperVConsoleController _hyperVConsoleController = new();
    private static readonly string DesktopSessionBrokerPath = Path.Combine(
        AppContext.BaseDirectory,
        "desktop-sessions",
        "broker",
        "YueYuOrb.SessionBroker.exe");
    private static readonly string DesktopSessionAgentPath = Path.Combine(
        AppContext.BaseDirectory,
        "desktop-sessions",
        "agent",
        "YueYuOrb.SessionAgent.exe");

    private static bool HasDesktopSessionBrokerPayload() =>
        File.Exists(DesktopSessionBrokerPath) &&
        File.Exists(DesktopSessionAgentPath) &&
        File.Exists(DesktopSessionAgentPath + ".sha256");

    private DesktopSessionBackendBundle CreateDesktopSessionBackendBundle(
        EnvironmentProfile environment)
    {
        if (environment.Backend == DesktopSessionBackendKind.HyperV)
        {
            var endpoints = new HyperVAgentEndpointRegistry();
            var secrets = new DpapiHyperVPairingSecretStore();
            var backend = new HyperVSessionBackend(
                new PowerShellHyperVHost(),
                new HyperVGuestAgentProbe(secrets),
                endpoints,
                showView: (id, token) =>
                {
                    var current = _settings.Current.DesktopSessions.Environments.FirstOrDefault(item => item.Id == environment.Id) ?? environment;
                    return _hyperVConsoleController.ShowAsync(id, current.DesktopWidth, current.DesktopHeight,
                        current.HostSharingEnabled ? current.HostSharedPaths : [],
                        () => OnHyperVConsoleClosedAsync(environment.Id, id), token,
                        saveResolution: async (width, height) =>
                        {
                            var result = await _settings.UpdateAsync(settings => settings with
                            {
                                DesktopSessions = DesktopSessionResolutionPreferences.Update(settings.DesktopSessions, environment.Id, width, height)
                            }, _lifetime.Token);
                            if (!result.Success) throw new InvalidOperationException("分辨率偏好保存失败，请重试。");
                        },
                        environmentId: environment.Id,
                        useSavedLogin: current.UseAuthenticatedRdp,
                        openSettings: () =>
                        {
                            ShowSettings();
                            GetTool<FloatDesk.App.Views.SettingsWindow>()?.SelectDesktopSessionsPage();
                        });
                });
            var transport = new HyperVSocketTransport(
                new WindowsHyperVSocketConnector(),
                endpoints,
                secrets);
            return new DesktopSessionBackendBundle(backend, transport);
        }

        var bootstrapStore = _desktopSessionBootstrapStore ??
            throw new InvalidOperationException(
                "DesktopSessionBrokerPayloadUnavailable");
        var native = new ChildSessionNative();
        var launcher = new RdpChildSessionLauncher(
            native,
            agentBootstrapper: new ServiceStartingSessionBroker(),
            bootstrapStore: bootstrapStore,
            stopEnvironment: async id =>
            {
                await LocalSessionBrokerLifecycle.SetRunningAsync(false);
                return await _desktopSessions.StopEnvironmentAsync(id, CancellationToken.None);
            },
            saveResolution: async (id, width, height) =>
            {
                var result = await _settings.UpdateAsync(current => current with
                {
                    DesktopSessions = DesktopSessionResolutionPreferences.Update(current.DesktopSessions, id, width, height)
                }, _lifetime.Token);
                if (!result.Success)
                    throw new InvalidOperationException("分辨率已切换，但保存失败，请在设置中重试。");
            });
        return new DesktopSessionBackendBundle(
            new ChildSessionBackend(native, launcher),
            new NamedPipeSessionTransport(
                handle => bootstrapStore.Take(handle)));
    }

    /// <summary>
    /// Applies the configured close action for a console that is being closed. Runs while the
    /// console window is still open so a failure can be shown and retried.
    /// </summary>
    private async Task<string> OnHyperVConsoleClosedAsync(Guid environmentId, Guid vmId)
    {
        var environment = _settings.Current.DesktopSessions.Environments.FirstOrDefault(item => item.Id == environmentId);
        if (environment is null || environment.ConsoleCloseAction == HyperVConsoleCloseAction.CloseView)
            return "控制台已关闭，虚拟机继续运行。";
        try
        {
            var stopped = await _desktopSessions.StopEnvironmentAsync(environmentId, _lifetime.Token).ConfigureAwait(false);
            if (!stopped.Success && _desktopSessions.IsEnvironmentReady(environmentId))
                throw new InvalidOperationException(stopped.Message);
            await new HyperVVirtualMachineManager().ApplyCloseActionAsync(
                vmId, environment.ConsoleCloseAction, _lifetime.Token).ConfigureAwait(false);
            await _logger.WriteAsync(
                "desktop-session.console.close-policy",
                new Dictionary<string, object?>
                {
                    ["environmentId"] = environmentId,
                    ["action"] = environment.ConsoleCloseAction.ToString()
                }).ConfigureAwait(false);
            return environment.ConsoleCloseAction == HyperVConsoleCloseAction.SaveState
                ? "已保存虚拟机状态并释放运行资源。"
                : "已关闭虚拟机。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return "已跳过关闭设置（应用正在退出）。";
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync(
                "desktop-session.console.close-policy.failed",
                new Dictionary<string, object?>
                {
                    ["environmentId"] = environmentId,
                    ["action"] = environment.ConsoleCloseAction.ToString(),
                    ["errorType"] = exception.GetType().Name
                }).ConfigureAwait(false);
            _tray?.ShowWarning("桌面分身关闭策略未完成", exception.Message);
            // Rethrown so the console window keeps the message on screen and lets the user retry.
            throw;
        }
    }

    private void OnDesktopSessionsRequested()
    {
        _panel?.HideAnimated();
        var candidates = DesktopSessionLaunchSelection.GetCandidates(_settings.Current.DesktopSessions);
        if (candidates.Count == 1)
        {
            _ = StartDesktopSessionFromWheelAsync(candidates[0].Id);
            return;
        }
        if (candidates.Count > 1)
        {
            ShowDesktopSessionPicker(candidates);
            return;
        }
        ShowSettings();
        GetTool<FloatDesk.App.Views.SettingsWindow>()
            ?.SelectDesktopSessionsPage();
    }

    private void ShowDesktopSessionPicker(IReadOnlyList<EnvironmentProfile> candidates)
    {
        if (_desktopSessionPicker is not null)
            _desktopSessionPicker.IsOpen = false;
        var options = new System.Windows.Controls.StackPanel();
        var border = new System.Windows.Controls.Border
        {
            Child = options, Padding = new System.Windows.Thickness(10), CornerRadius = new System.Windows.CornerRadius(14),
            BorderThickness = new System.Windows.Thickness(1), MaxHeight = 420, MinWidth = 230
        };
        border.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative)
        });
        border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SettingsCardBrush");
        border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "SettingsLineBrush");
        border.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SettingsInkBrush");
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            Child = border, PlacementTarget = _orb, Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
            StaysOpen = false, AllowsTransparency = true
        };
        options.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "选择桌面分身", Margin = new System.Windows.Thickness(10, 5, 10, 10),
            FontWeight = System.Windows.FontWeights.SemiBold
        });
        var rows = new System.Windows.Controls.StackPanel();
        options.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            Content = rows, MaxHeight = 350, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled
        });
        foreach (var environment in candidates)
        {
            var button = new System.Windows.Controls.Button
            {
                Content = environment.Name,
                ToolTip = environment.Backend == DesktopSessionBackendKind.LocalChild ? "本机分身" : "Hyper-V 分身",
                Style = (System.Windows.Style)border.FindResource("ActionButtonStyle"),
                Margin = new System.Windows.Thickness(0, 3, 0, 3),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left
            };
            button.Click += (_, _) => { popup.IsOpen = false; _ = StartDesktopSessionFromWheelAsync(environment.Id); };
            rows.Children.Add(button);
        }
        _desktopSessionPicker = popup;
        popup.IsOpen = true;
    }

    private async Task StartDesktopSessionFromWheelAsync(Guid environmentId)
    {
        try
        {
            var result = await _desktopSessions.StartEnvironmentAsync(environmentId, _lifetime.Token);
            if (!result.Success)
                _tray?.ShowWarning("桌面分身", result.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { _tray?.ShowWarning("桌面分身", $"启动失败：{exception.Message}"); }
    }

    private void OnDesktopTaskRequested(Guid taskId) =>
        _ = RunDesktopTaskFromWheelAsync(taskId);

    private async Task RunDesktopTaskFromWheelAsync(Guid taskId)
    {
        _panel?.HideAnimated();
        try
        {
            var status = await _desktopSessions.RunTaskAsync(
                taskId,
                _lifetime.Token);
            _tray?.ShowWarning("桌面分身", status.Message);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync(
                "desktop-session.wheel-task.failed",
                new Dictionary<string, object?>
                {
                    ["errorType"] = exception.GetType().Name
                });
            _tray?.ShowWarning(
                "桌面分身",
                "任务请求失败，请查看桌面分身设置中的最近活动。");
        }
    }

    private async void OnDesktopSessionTimerTick(
        object? sender,
        EventArgs e)
    {
        if (_disposed)
            return;
        try
        {
            await _desktopSessions.ProcessScheduledAsync(
                _lifetime.Token);
        }
        catch (OperationCanceledException)
            when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.WriteAsync(
                "desktop-session.schedule.failed",
                new Dictionary<string, object?>
                {
                    ["errorType"] = exception.GetType().Name
                });
        }
    }
}
