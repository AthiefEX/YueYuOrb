using System.Diagnostics;
using System.Runtime.InteropServices;
using FloatDesk.Windows.DesktopSessions;
using FloatDesk.App.Views;
using System.Windows;

namespace FloatDesk.App.DesktopSessions;

internal sealed class HyperVConsoleController : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, ConsoleEntry> _windows = [];
    private readonly CancellationTokenSource _shutdown = new();

    public async Task ShowAsync(Guid vmId, int width, int height, IReadOnlyList<string> sharedPaths, Func<Task<string>> onClosed, CancellationToken cancellationToken,
        Func<int, int, Task>? saveResolution = null, Action? openSettings = null, Guid? environmentId = null, bool useSavedLogin = true)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_windows.TryGetValue(vmId, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    if (existing.Window.IsConsoleClosed)
                        throw new InvalidOperationException("分身控制台正在执行关闭策略，请稍后重试。");
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        existing.Window.WindowState = WindowState.Normal;
                        existing.Window.Show();
                        existing.Window.Activate();
                    });
                    return;
                }
                if (!existing.Closed.Task.IsCompleted)
                    throw new InvalidOperationException("分身控制台正在执行关闭策略，请稍后重试。");
                await Application.Current.Dispatcher.InvokeAsync(existing.Window.Close);
                existing.Process.Dispose();
                _windows.Remove(vmId);
            }
            var machines = await new HyperVVirtualMachineManager().ListAsync(cancellationToken).ConfigureAwait(false);
            var vm = machines.FirstOrDefault(item => item.Id == vmId)
                ?? throw new InvalidOperationException("目标 Hyper-V 虚拟机不存在，请刷新环境配置。");
            cancellationToken.ThrowIfCancellationRequested();
            var sharing = HyperVSharedDriveLease.Create(sharedPaths);
            Process process;
            AuthenticatedRdpConnection? authenticated = null;
            try
            {
                using var saved = useSavedLogin ? new WindowsGuestCredentialStore().Load(vmId) : null;
                if (saved is not null)
                {
                    authenticated = await AuthenticatedRdpConnection.CreateAsync(vmId, width, height, sharing.Drives, cancellationToken).ConfigureAwait(false);
                    process = Process.Start(authenticated.StartInfo) ?? throw new InvalidOperationException("无法启动认证桌面客户端。");
                }
                else
                {
                    await new PowerShellHyperVHost().RunScriptForManagementAsyncCore(
                        HyperVConsoleSettings.BuildDisplayScript(vmId, width, height, sharing.Drives), cancellationToken).ConfigureAwait(false);
                    process = Process.Start(HyperVVirtualMachineManager.BuildVmConnectCommand(vm.Name, embedded: true))
                    ?? throw new InvalidOperationException("无法打开 Hyper-V 控制台，请检查管理工具。");
                }
            }
            catch { authenticated?.Dispose(); sharing.Dispose(); throw; }
            HyperVConsoleWindow? window = null;
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    window = new HyperVConsoleWindow(vmId, vm.Name, process, width, height);
                    window.OpenSettings = openSettings;
                    // The close action runs while this window is still open, so a failed save can be
                    // shown and retried instead of being inferred from the console process exit code.
                    window.ApplyClosePolicyAsync = onClosed;
                    if (authenticated is not null)
                    {
                        window.AuthenticationNotice = $"认证连接 · 首次核对 {authenticated.GuestComputerName} 证书，指纹 {authenticated.CertificateFingerprint}";
                        process.OutputDataReceived += (_, args) =>
                        {
                            if (args.Data == "RdpLoginComplete") Application.Current.Dispatcher.BeginInvoke(() =>
                            {
                                if (!window.IsConsoleClosed) window.AuthenticationNotice = null;
                            });
                        };
                        process.ErrorDataReceived += (_, args) =>
                        {
                            if (args.Data is { Length: < 160 } error && error.StartsWith("Rdp", StringComparison.Ordinal))
                                Application.Current.Dispatcher.BeginInvoke(() => { if (!window.IsConsoleClosed) window.NativeStartupError = error; });
                        };
                        process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    }
                    if (environmentId is { } envId)
                        window.RepairNetworkAsync = progress => new HyperVNetworkRepair().RepairAsync(vmId, envId, progress, _shutdown.Token);
                    window.SaveDisplayAsync = async (newWidth, newHeight) =>
                    {
                        await new PowerShellHyperVHost().RunScriptForManagementAsyncCore(
                            HyperVConsoleSettings.BuildDisplayScript(vmId, newWidth, newHeight, sharing.Drives), _shutdown.Token);
                        if (saveResolution is not null) await saveResolution(newWidth, newHeight);
                    };
                    window.Show();
                });
                await window!.Attached.ConfigureAwait(false);
            }
            catch
            {
                authenticated?.Dispose(); sharing.Dispose();
                if (!process.HasExited && !process.CloseMainWindow()) process.Kill();
                if (window is null) process.Dispose();
                else await Application.Current.Dispatcher.InvokeAsync(() => window.Closed += (_, _) => process.Dispose());
                throw;
            }
            var entry = new ConsoleEntry(process, sharing, window!, authenticated);
            _windows[vmId] = entry;
            _ = ObserveCloseAsync(entry);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var entry in _windows.Values)
        {
            // Application shutdown must not cancel its own close to save a virtual machine the
            // user did not ask to stop; the window closes without applying the close policy.
            entry.Window.SkipClosePolicy = true;
            Application.Current.Dispatcher.Invoke(entry.Window.Close);
            entry.Sharing.Dispose(); entry.Process.Dispose();
            entry.Authenticated?.Dispose();
        }
        _windows.Clear();
        _gate.Dispose();
        _shutdown.Dispose();
    }

    private async Task ObserveCloseAsync(ConsoleEntry entry)
    {
        var token = _shutdown.Token;
        try
        {
            while (!entry.Process.HasExited)
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            entry.Sharing.Dispose();
            entry.Authenticated?.Dispose();
            // The close action is applied by the window itself; waiting for the pending power
            // operation here keeps the driven-shutdown path from releasing the drive lease while
            // its readback is still in flight.
            await entry.Window.PowerOperationCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        finally { entry.Authenticated?.Dispose(); entry.Sharing.Dispose(); entry.Closed.TrySetResult(); }
    }

    private sealed class ConsoleEntry(Process process, HyperVSharedDriveLease sharing, HyperVConsoleWindow window, AuthenticatedRdpConnection? authenticated)
    {
        public Process Process { get; } = process;
        public HyperVSharedDriveLease Sharing { get; } = sharing;
        public HyperVConsoleWindow Window { get; } = window;
        public AuthenticatedRdpConnection? Authenticated { get; } = authenticated;
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
