using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
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
        if (environment.Backend != DesktopSessionBackendKind.LocalChild)
        {
            throw new NotSupportedException(
                "HyperVProductionBackendUnavailable");
        }

        var bootstrapStore = _desktopSessionBootstrapStore ??
            throw new InvalidOperationException(
                "DesktopSessionBrokerPayloadUnavailable");
        var native = new ChildSessionNative();
        var launcher = new RdpChildSessionLauncher(
            native,
            agentBootstrapper: new SessionBrokerClient(),
            bootstrapStore: bootstrapStore);
        return new DesktopSessionBackendBundle(
            new ChildSessionBackend(native, launcher),
            new NamedPipeSessionTransport(
                handle => bootstrapStore.Take(handle)));
    }

    private void OnDesktopSessionsRequested()
    {
        _panel?.HideAnimated();
        ShowSettings();
        GetTool<FloatDesk.App.Views.SettingsWindow>()
            ?.SelectDesktopSessionsPage();
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
