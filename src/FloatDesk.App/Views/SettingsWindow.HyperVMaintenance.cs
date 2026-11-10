using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TextBox _guestLoginUser = null!;
    private PasswordBox _guestLoginPassword = null!;
    private TextBlock _guestMaintenanceStatus = null!;
    private StackPanel _guestMaintenanceActions = null!;
    private bool _guestMaintenanceBusy;
    private readonly WindowsGuestCredentialStore _guestCredentials = new();
    private Guid? _guestCredentialVmId;
    private CheckBox _guestAutoLogonToggle = null!;
    private TextBlock _guestAutoLogonState = null!;
    private int _guestAutoLogonReadVersion;
    private TextBlock _guestMaintenanceTarget = null!;

    private StackPanel BuildGuestMaintenancePanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(SectionTitle("网络与自动登录"));
        _guestMaintenanceTarget = Description("目标虚拟机：尚未选择。");
        panel.Children.Add(_guestMaintenanceTarget);
        panel.Children.Add(Description("针对上方选中的虚拟机立即操作。网络修复需要 Agent 已配对，可能短暂断网；不会关闭虚拟机。"));
        _guestLoginUser = AddVmInput(panel, "来宾本地用户名（例如 YueYu，不含域名）", "");
        panel.Children.Add(Description("来宾密码（留空保留已保存凭据）"));
        _guestLoginPassword = new PasswordBox { Margin = new Thickness(0, 3, 0, 8), Style = (Style)FindResource("SettingsPasswordBoxStyle") };
        panel.Children.Add(_guestLoginPassword);
        _guestMaintenanceActions = new StackPanel();
        _guestMaintenanceActions.Children.Add(ButtonRow(
            Button("一键配置自动登录", () => _ = RunGuestMaintenanceAsync("prepare-login")),
            Button("修复网络", () => _ = RunGuestMaintenanceAsync("network")),
            Button("测试并保存凭据", () => _ = RunGuestMaintenanceAsync("save"))));
        _guestMaintenanceActions.Children.Add(ButtonRow(
            Button("清除主机凭据", () => _ = RunGuestMaintenanceAsync("forget"))));
        _guestAutoLogonToggle = new CheckBox { Content = "开机自动登录", IsThreeState = true, IsChecked = null, IsEnabled = false,
            Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 10, 0, 6) };
        _guestAutoLogonToggle.Click += async (_, _) => await RunGuestMaintenanceAsync(_guestAutoLogonToggle.IsChecked == true ? "enable" : "disable");
        _guestMaintenanceActions.Children.Add(_guestAutoLogonToggle);
        _guestAutoLogonState = Description("请先选择虚拟机并一键配置，或读取已保存凭据对应的状态。");
        _guestMaintenanceActions.Children.Add(_guestAutoLogonState);
        _guestMaintenanceActions.Children.Add(Button("撤销来宾 RDP 配置", () => _ = RunGuestMaintenanceAsync("remove-rdp")));
        panel.Children.Add(_guestMaintenanceActions);
        panel.Children.Add(Description("一键配置使用填写或已保存的凭据，必要时启动／恢复所选虚拟机，验证并准备登录入口；完成后可自行开启下方开关。不会自动重启或注销当前会话。"));
        panel.Children.Add(Description("凭据按虚拟机保存在 Windows 凭据管理器；自动登录密码保存在来宾 LSA 密钥中，管理员可读取。开机自动登录下次冷启动生效；认证连接在保存凭据后使用，首次需核对 Windows 证书。仅支持单网卡 Default Switch，可使用原生控制台回退。清除主机凭据不会关闭来宾自动登录。"));
        panel.Children.Add(Description("如要停用认证连接，请先取消环境中的自动进入桌面开关并保存，再撤销来宾 RDP 配置；否则下次打开会重新配置。"));
        panel.Children.Add(Description("关闭控制台建议在环境设置中选择“保存状态”，下次打开会自动恢复。"));
        _guestMaintenanceStatus = Description("请选择虚拟机。"); panel.Children.Add(_guestMaintenanceStatus);
        return panel;
    }

    private void RefreshGuestCredentialSummary(Guid? vmId)
    {
        if (_guestLoginPassword is null) return;
        if (vmId is not null)
            _guestMaintenanceTarget.Text = "目标虚拟机：" + ((_hyperVVmList.SelectedItem as HyperVVirtualMachine)?.Name ?? vmId.Value.ToString("D"));
        else if (!_vmRefreshRunning) _guestMaintenanceTarget.Text = "目标虚拟机：尚未选择。";
        // List polling temporarily clears/reselects the same VM. Never clear a
        // password being typed just because a fresh VM status arrived.
        if (vmId == _guestCredentialVmId || (vmId is null && _vmRefreshRunning)) return;
        _guestCredentialVmId = vmId;
        ++_guestAutoLogonReadVersion;
        SetGuestAutoLogonState(null, "自动登录状态尚未读取。");
        _guestLoginPassword.Clear(); _guestLoginUser.Clear();
        if (vmId is null) { _guestMaintenanceStatus.Text = "请选择虚拟机。"; return; }
        try
        {
            using var saved = _guestCredentials.Load(vmId.Value);
            if (saved is not null) _guestLoginUser.Text = saved.UserName;
            _guestMaintenanceStatus.Text = saved is null ? "此虚拟机尚未保存来宾凭据。" : "此虚拟机已保存凭据；留空密码不会覆盖。";
            if (saved is not null) _ = LoadGuestAutoLogonStateAsync(vmId.Value);
        }
        catch (Exception) { _guestMaintenanceStatus.Text = "读取凭据失败，请重新设置。"; }
    }

    private async Task RunGuestMaintenanceAsync(string operation)
    {
        if (_guestMaintenanceBusy || _environmentPowerBusy || _vmGpuBusy) return;
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        { _guestMaintenanceStatus.Text = "请先选择虚拟机。"; return; }
        _guestMaintenanceBusy = true;
        ++_guestAutoLogonReadVersion;
        _guestMaintenanceActions.IsEnabled = false; _hyperVVmList.IsEnabled = false;
        _desktopVmActionRow.IsEnabled = false;
        _desktopAdvancedTabs.IsEnabled = _desktopHyperVOptions.IsEnabled = false;
        _desktopEnvironmentList.IsEnabled = _desktopBackendKind.IsEnabled = _desktopBackendProfile.IsEnabled = _desktopEnvironmentFilter.IsEnabled = false;
        _guestLoginUser.IsEnabled = _guestLoginPassword.IsEnabled = false;
        try
        {
            _guestMaintenanceStatus.Text = "正在处理，请稍候…";
            if (operation == "prepare-login")
            {
                using var entered = _guestLoginPassword.SecurePassword;
                using var candidate = entered.Length > 0 ? new GuestCredential(_guestLoginUser.Text.Trim(), entered.Copy()) :
                    _guestCredentials.Load(vm.Id) ?? throw new InvalidOperationException("请先填写来宾本地用户名和密码，再点击一键配置。无需在聊天中发送密码。");
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                var state = await new HyperVAutoLogonWorkflow(_guestCredentials).PrepareAsync(vm.Id, candidate, deadline.Token);
                _guestLoginUser.Text = candidate.UserName;
                SetGuestAutoLogonState(state);
                _guestMaintenanceStatus.Text = state == GuestAutoLogonState.Foreign
                    ? "登录入口与凭据已准备，但来宾已有其他来源的自动登录配置，月语未覆盖；开关不可用。"
                    : "登录入口与凭据已配置完成，可自行选择“开机自动登录”开关。首次连接仍需确认系统证书。";
                return;
            }
            if (operation == "network")
            {
                var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault(item =>
                    item.Backend == DesktopSessionBackendKind.HyperV && Guid.TryParse(item.BackendProfileId, out var id) && id == vm.Id)
                    ?? throw new InvalidOperationException("请先关联并保存此虚拟机的环境，完成 Agent 配对。");
                var result = await new HyperVNetworkRepair().RepairAsync(vm.Id, environment.Id,
                    new Progress<string>(message => _guestMaintenanceStatus.Text = message), CancellationToken.None);
                _guestMaintenanceStatus.Text = result.Summary;
                return;
            }
            if (operation == "forget")
            {
                _guestCredentials.Delete(vm.Id);
                _guestMaintenanceStatus.Text = "主机凭据已清除；来宾自动登录设置未更改。";
                return;
            }
            if (operation == "save")
            {
                using var password = _guestLoginPassword.SecurePassword;
                if (password.Length == 0) throw new InvalidOperationException("请在密码框填写来宾密码，再测试保存。已保存凭据未更改。");
                using var candidate = new GuestCredential(_guestLoginUser.Text.Trim(), password.Copy());
                // Validation before any connection and before replacing a known-good credential.
                if (candidate.UserName.Length == 0 || candidate.UserName.Any(c => char.IsControl(c) || "\\/@".Contains(c)))
                    throw new InvalidOperationException("请填写来宾本地用户名，不含域名。");
                await HyperVGuestAdministration.RunAsync(vm.Id, candidate, GuestAdministrationOperation.TestCredential, CancellationToken.None);
                _guestCredentials.Save(vm.Id, candidate.UserName, candidate.Password);
                _guestMaintenanceStatus.Text = "来宾凭据验证通过并已加密保存。可以启用开机自动登录。";
                return;
            }
            using var credential = _guestCredentials.Load(vm.Id) ?? throw new InvalidOperationException("请先测试并保存此虚拟机的来宾凭据。");
            if (operation is "enable" or "disable")
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                var state = await new HyperVAutoLogonWorkflow(_guestCredentials).SetEnabledAsync(vm.Id, credential, operation == "enable", deadline.Token);
                await SaveAssociatedLoginPreferenceAsync(vm.Id, operation == "enable");
                SetGuestAutoLogonState(state);
            }
            else await HyperVGuestAdministration.RunAsync(vm.Id, credential, GuestAdministrationOperation.DisableAuthenticatedRdp, CancellationToken.None);
            _guestMaintenanceStatus.Text = operation == "remove-rdp" ? "月语来宾 RDP 规则已移除，远程桌面开关已恢复原值。" : operation == "enable" ? "开机自动登录已启用，下次冷启动生效。现有会话未注销。" : "来宾自动登录已关闭，相关 LSA 密钥已清除。";
        }
        catch (Exception exception) { _guestMaintenanceStatus.Text = exception.Message; }
        finally
        {
            _guestLoginPassword.Clear();
            _guestMaintenanceBusy = false; _guestMaintenanceActions.IsEnabled = _hyperVVmList.IsEnabled = true;
            _desktopVmActionRow.IsEnabled = true;
            _desktopAdvancedTabs.IsEnabled = _desktopHyperVOptions.IsEnabled = true;
            _desktopEnvironmentList.IsEnabled = _desktopBackendKind.IsEnabled = _desktopBackendProfile.IsEnabled = _desktopEnvironmentFilter.IsEnabled = true;
            _guestLoginUser.IsEnabled = _guestLoginPassword.IsEnabled = true;
            await LoadGuestAutoLogonStateAsync(vm.Id);
        }
    }

    private void SetGuestAutoLogonState(GuestAutoLogonState? state, string? message = null)
    {
        if (_guestAutoLogonToggle is null) return;
        _guestAutoLogonToggle.IsChecked = state == GuestAutoLogonState.Enabled ? true : state == GuestAutoLogonState.Disabled ? false : null;
        _guestAutoLogonToggle.IsEnabled = !_guestMaintenanceBusy && state is GuestAutoLogonState.Enabled or GuestAutoLogonState.Disabled;
        _guestAutoLogonState.Text = message ?? (state == GuestAutoLogonState.Enabled ? "已验证：自动登录已开启。" :
            state == GuestAutoLogonState.Disabled ? "已验证：自动登录已关闭。" : "检测到其他来源的自动登录配置，月语不会覆盖。");
    }

    private async Task LoadGuestAutoLogonStateAsync(Guid vmId)
    {
        var revision = ++_guestAutoLogonReadVersion;
        try
        {
            using var credential = _guestCredentials.Load(vmId);
            if (credential is null)
            { if (_guestCredentialVmId == vmId) SetGuestAutoLogonState(null, "请先一键配置自动登录。"); return; }
            var state = await new HyperVAutoLogonWorkflow(_guestCredentials).ReadStateAsync(vmId, credential, CancellationToken.None);
            if (revision == _guestAutoLogonReadVersion && _guestCredentialVmId == vmId) SetGuestAutoLogonState(state);
        }
        catch (Exception)
        {
            if (revision == _guestAutoLogonReadVersion && _guestCredentialVmId == vmId)
                SetGuestAutoLogonState(null, "状态未确认。可点击一键配置，必要时会启动此虚拟机。 ");
        }
    }

    private async Task SaveAssociatedLoginPreferenceAsync(Guid vmId, bool enabled)
    {
        bool Matches(EnvironmentProfile environment) => environment.Backend == DesktopSessionBackendKind.HyperV &&
            Guid.TryParse(environment.BackendProfileId, out var id) && id == vmId;
        foreach (var environment in _viewModel.DesktopSessionEnvironments.Where(Matches).ToArray())
            _viewModel.UpdateDesktopSessionEnvironment(environment.Id, item => item with { UseAuthenticatedRdp = enabled });
        if (_desktopEnvironmentList.SelectedItem is EnvironmentProfile current && Matches(current)) _desktopUseSavedLogin.IsChecked = enabled;
        var saved = await _settings.UpdateAsync(current => current with { DesktopSessions = current.DesktopSessions with
        { Environments = current.DesktopSessions.Environments.Select(item => Matches(item) ? item with { UseAuthenticatedRdp = enabled } : item).ToArray() } }, CancellationToken.None);
        if (!saved.Success) throw new InvalidOperationException("来宾自动登录已更改，但桌面入口偏好保存失败，请点击应用重试。");
        Applied?.Invoke(_settings.Current);
    }
}
