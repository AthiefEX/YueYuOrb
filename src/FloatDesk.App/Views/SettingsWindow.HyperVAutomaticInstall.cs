using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TextBox _vmInstallUser = null!;
    private PasswordBox _vmInstallPassword = null!;
    private TextBox _vmImageIndex = null!;
    private bool _vmInstalling;
    private TextBlock _vmInstallProgress = null!;
    private ProgressBar _vmInstallBar = null!;
    private CheckBox _vmInstallAutoLogon = null!;

    private StackPanel BuildAutomaticInstallFields()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(SectionTitle("全自动 Windows 安装"));
        panel.Children.Add(Description("选择官方 x64 Windows ISO。自动部署到新建磁盘、创建本地账户、首次登录并初始化 Agent。需要以管理员身份运行月语。"));
        _vmImageIndex = AddVmInput(panel, "ISO 中的 Windows 镜像索引（请与 ISO 版本对应）", "1");
        _vmInstallUser = AddVmInput(panel, "新建来宾本地用户名", "YueYu");
        panel.Children.Add(Description("来宾密码（不会保存到普通设置；开启自动登录时将保存到 Windows 凭据管理器及来宾 LSA）"));
        _vmInstallPassword = new PasswordBox { Margin = new Thickness(0, 3, 0, 8), Style = (Style)FindResource("SettingsPasswordBoxStyle") };
        panel.Children.Add(_vmInstallPassword);
        _vmInstallAutoLogon = new CheckBox { Content = "安装后启用自动登录", IsChecked = false,
            Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(_vmInstallAutoLogon);
        panel.Children.Add(Description("开启后自动配置开机登录与保存凭据的桌面入口；首次连接仍需核对系统证书。关闭时仅临时登录一次完成初始化，不保留持续自动登录。"));
        panel.Children.Add(Button("选择 ISO 并自动安装", () => _ = InstallWindowsAutomaticallyAsync()));
        _vmInstallProgress = Description("尚未开始自动安装。");
        _vmInstallBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(_vmInstallBar);
        panel.Children.Add(_vmInstallProgress);
        return panel;
    }

    private async Task InstallWindowsAutomaticallyAsync()
    {
        if (_vmInstalling) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "选择自动安装用 Windows ISO", Filter = "Windows ISO|*.iso" };
        if (dialog.ShowDialog(this) != true) return;
        _vmInstalling = true;
        _vmInstallBar.IsIndeterminate = true;
        try
        {
            var options = ReadHyperVCreationFields();
            if (!int.TryParse(_vmImageIndex.Text, out var index) || index < 1)
                throw new InvalidOperationException("请输入有效的 Windows 镜像索引。");
            var name = "月语自动分身-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var environmentId = Guid.NewGuid();
            var request = new HyperVVmCreateRequest(environmentId, name, Path.Combine(options.StorageDirectory, name), dialog.FileName,
                options.CpuCount, options.MemoryStartupMiB, options.DiskSizeGiB, options.SwitchName, options.SecureBoot);
            var user = _vmInstallUser.Text.Trim();
            var autoLogon = _vmInstallAutoLogon.IsChecked == true;
            using var installationPassword = _vmInstallPassword.SecurePassword;
            var password = _vmInstallPassword.Password;
            _vmInstallPassword.Clear();
            var progress = new Progress<string>(stage =>
            {
                if (stage.StartsWith("PROGRESS:", StringComparison.Ordinal) &&
                    double.TryParse(stage.AsSpan(9), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                {
                    _vmInstallBar.IsIndeterminate = false;
                    _vmInstallBar.Value = Math.Clamp(percent, 0, 100);
                    _vmInstallProgress.Text = $"Windows 镜像应用进度：{percent:0.0}%";
                }
                else { _vmInstallProgress.Text = stage; _vmInstallBar.IsIndeterminate = true; }
            });
            var vmId = await HyperVAutomaticInstaller.InstallAsync(request, index, user, password,
                Path.Combine(AppContext.BaseDirectory, "desktop-sessions", "agent"), progress, startAfterDeploy: false);
            password = string.Empty;
            if (autoLogon) _guestCredentials.Save(vmId, user, installationPassword);
            var environment = new EnvironmentProfile(environmentId, name, DesktopSessionBackendKind.HyperV,
                vmId.ToString("D"), SessionStartupPolicy.EnvironmentDefault, new SessionLimits(), []) { UseAuthenticatedRdp = autoLogon };
            var saved = await _settings.UpdateAsync(current => current with
            {
                DesktopSessions = current.DesktopSessions with
                {
                    Enabled = true,
                    HyperV = new BackendModeSettings(true, current.DesktopSessions.HyperV?.DefaultEnvironmentId ?? environmentId),
                    Environments = current.DesktopSessions.Environments.Append(environment).ToArray()
                }
            }, CancellationToken.None);
            if (!saved.Success) throw new InvalidOperationException("系统已部署，但保存新环境失败，请保留虚拟机并手动添加环境。");
            Applied?.Invoke(_settings.Current);
            _viewModel.AddDesktopSessionEnvironment(environment);
            _viewModel.HyperVDesktopSessionEnabled = true;
            string? gpuWarning = null;
            if (options.GpuMode == HyperVGpuMode.GpuPartition)
            {
                _vmInstallProgress.Text = "Windows 已部署，正在首次启动前配置显卡共享…";
                try
                {
                    await HyperVGpuSharing.ApplyAsync(vmId, true, CancellationToken.None);
                    _viewModel.UpdateDesktopSessionEnvironment(environmentId, item => item with { GpuSharingEnabled = true });
                    var gpuSaved = await _settings.UpdateAsync(current => current with
                    {
                        DesktopSessions = current.DesktopSessions with { Environments = current.DesktopSessions.Environments
                            .Select(item => item.Id == environmentId ? item with { GpuSharingEnabled = true } : item).ToArray() }
                    }, CancellationToken.None);
                    if (!gpuSaved.Success) gpuWarning = "显卡配置已生效，但偏好保存失败，请点击应用。";
                }
                catch (Exception exception) { gpuWarning = $"显卡共享未启用：{exception.Message}；可在环境设置中重试。"; }
            }
            await _hyperVVmManager.StartAsync(vmId, CancellationToken.None);
            RefreshDesktopSessionsPage();
            await RefreshHyperVVmsAsync();
            _vmInstallProgress.Text = "新环境已保存，等待 Windows 首次登录和 Agent 自动初始化…";
            var probe = new HyperVGuestAgentProbe(new DpapiHyperVPairingSecretStore());
            using var readyTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            while (!readyTimeout.IsCancellationRequested)
            {
                try
                {
                    await probe.ProbeAsync(vmId, readyTimeout.Token);
                    using var loginCredential = new GuestCredential(user, installationPassword.Copy());
                    if (autoLogon && await HyperVGuestAdministration.RunAsync(vmId, loginCredential,
                        GuestAdministrationOperation.QueryBootstrapState, readyTimeout.Token) != "GuestBootstrapState:Complete")
                        throw new InvalidOperationException("等待首次登录初始化清理完成。");
                    string? loginWarning = null;
                    if (autoLogon)
                    {
                        _vmInstallProgress.Text = "Windows 已就绪，正在自动配置受保护的登录入口…";
                        try
                        {
                            var workflow = new HyperVAutoLogonWorkflow(_guestCredentials);
                            await workflow.PrepareAsync(vmId, loginCredential, readyTimeout.Token);
                            await workflow.SetEnabledAsync(vmId, loginCredential, true, readyTimeout.Token);
                        }
                        catch (Exception exception) { loginWarning = "自动登录未完成，可在高级设置中一键重试：" + exception.Message; }
                    }
                    _vmInstallProgress.Text = "Windows 安装及 Agent 身份验证完成。虚拟机已就绪。" +
                        (autoLogon && loginWarning is null ? "自动登录已配置。" : loginWarning) + gpuWarning;
                    break;
                }
                catch (Exception exception) when (exception is System.IO.IOException or System.Net.Sockets.SocketException or InvalidOperationException or OperationCanceledException)
                {
                    if (readyTimeout.IsCancellationRequested) break;
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            if (readyTimeout.IsCancellationRequested)
                _vmInstallProgress.Text = "Windows 镜像已部署并保存环境，但首次登录／Agent 初始化等待超时。请打开控制台查看来宾当前阶段。" + gpuWarning;
            _vmInstallBar.Value = 100;
        }
        catch (Exception exception) { _vmInstallProgress.Text = $"自动安装未完成：{exception.Message}"; }
        finally { _vmInstalling = false; _vmInstallBar.IsIndeterminate = false; }
    }
}
