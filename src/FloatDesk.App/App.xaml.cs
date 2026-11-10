using FloatDesk.Core;
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
        if (e.Args.Length == 2 && e.Args[0] == "--steam-background-probe" && e.Args[1] is "status" or "cycle")
        {
            Shutdown(await SteamBackgroundNativeProbe.RunAsync(e.Args[1]));
            return;
        }
        if (e.Args is ["--steam-background-service"])
        {
            Shutdown(FloatDesk.Windows.Network.SteamBackgroundServiceHost.Run());
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--steam-background-install" or "--steam-background-uninstall")
        {
            var result = 1;
            try { result = await Task.Run(() => FloatDesk.Windows.Network.SteamBackgroundInstallation.Configure(e.Args[0] == "--steam-background-install", e.Args[1])); }
            catch (Exception error)
            {
                // Controlled setup diagnostics; never persist raw exception messages or arguments.
                try
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatDesk", "steam-background-setup.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { stage = FloatDesk.Windows.Network.SteamBackgroundInstallation.LastStage, errorType = error.GetType().Name, errorCode = error.HResult }));
                }
                catch (IOException) { }
            }
            Shutdown(result);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--steam-network-selftest" && e.Args[1] is "normal" or "hold" or "recover")
        {
            var probeResult = await SteamNetworkNativeProbe.RunAsync(e.Args[1]);
            Shutdown(probeResult);
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--steam-hosts-helper")
        {
            var exitCode = 1;
            try { exitCode = await Task.Run(() => FloatDesk.Windows.Network.SteamHostsHelper.Run(e.Args)); }
            catch (Exception) { /* No application UI or runtime is started by a helper. */ }
            Shutdown(exitCode);
            return;
        }
        _singleInstance = new SingleInstanceService();
        var lockResult = await _singleInstance.TryAcquireAsync(CancellationToken.None);
        if (!lockResult.Success)
        {
            if (lockResult.ErrorCode == "DataLockedOtherSession")
            {
                Shutdown();
                return;
            }
            if (lockResult.ErrorCode == "AlreadyRunning")
            {
                if (StartupService.ShouldNotifyExistingInstance(e.Args))
                    await SingleInstanceService.SignalPrimaryAsync(
                        CancellationToken.None);
                Shutdown();
                return;
            }
            System.Windows.MessageBox.Show(
                lockResult.Message ?? $"{ProductInfo.Name}已在运行。",
                ProductInfo.Name,
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
                $"{ProductInfo.Name}启动失败：{exception.Message}",
                ProductInfo.Name,
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
