using FloatDesk.Core.Contracts;
using System.Windows;

namespace FloatDesk.App;

public partial class App : System.Windows.Application, IDisposable
{
    private AppRuntime? _runtime;
    private SingleInstanceService? _singleInstance;

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _singleInstance = new SingleInstanceService();
        var lockResult = await _singleInstance.TryAcquireAsync(CancellationToken.None);
        if (!lockResult.Success)
        {
            if (lockResult.ErrorCode == "AlreadyRunning")
            {
                await SingleInstanceService.SignalPrimaryAsync(
                    CancellationToken.None);
                Shutdown();
                return;
            }
            System.Windows.MessageBox.Show(
                lockResult.Message ?? "FloatDesk 已在运行。",
                "FloatDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            _runtime = new AppRuntime(new AppDataPaths());
            await _runtime.InitializeAsync();
        }
        catch (Exception exception)
        {
            _runtime?.Dispose();
            _runtime = null;
            System.Windows.MessageBox.Show(
                $"FloatDesk 启动失败：{exception.Message}",
                "FloatDesk",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        _runtime?.Dispose();
        _runtime = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        GC.SuppressFinalize(this);
    }

    internal void ShowSettings() =>
        _runtime?.ShowTool(ToolWindow.Settings);
}
