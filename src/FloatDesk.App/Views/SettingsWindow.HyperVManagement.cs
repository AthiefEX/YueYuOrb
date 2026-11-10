using FloatDesk.Windows.DesktopSessions;
using FloatDesk.Core.Models;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Standalone console controller is disposed by the Window.Closed handler.")]
public partial class SettingsWindow
{
    private bool _hyperVFeatureOperationRunning;
    private TextBlock _hyperVFeatureStatus = null!;
    private ListBox _hyperVVmList = null!;
    private TextBlock _hyperVVmStatus = null!;
    private readonly HyperVVirtualMachineManager _hyperVVmManager = new();

    private Border BuildHyperVManagementCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("Hyper-V 系统组件与兼容性"));
        panel.Children.Add(Description(
            "启用可能影响其他虚拟机、模拟器及依赖硬件虚拟化的应用。操作需要管理员授权，通常需重启后生效；月语不会自动重启。"));
        panel.Children.Add(Description(
            "关闭仅针对 Hyper-V 系统组件，不自动修改 WSL、虚拟机平台、内存完整性或 Credential Guard；这些组件仍可能使用虚拟化，因此无法保证其他应用立即恢复兼容。"));
        _hyperVFeatureStatus = Description("系统状态未检测。可打开 Windows 功能查看已启用组件。");
        panel.Children.Add(_hyperVFeatureStatus);
        panel.Children.Add(ButtonRow(
            Button("安装/启用 Hyper-V", () => _ = ChangeHyperVFeatureAsync(true)),
            Button("关闭 Hyper-V", () => _ = ChangeHyperVFeatureAsync(false)),
            Button("打开 Windows 功能", OpenWindowsFeatures)));
        panel.Children.Add(Description(
            "启用 Hyper-V 不会自动创建 Windows 虚拟机或安装来宾系统。移除月语中的环境配置只删除关联配置及任务，保留现有 VM 和虚拟磁盘。"));
        var groups = new StackPanel();
        groups.Children.Add(panel);
        groups.Children.Add(BuildVirtualMachineManagementPanel());
        card.Child = groups;
        _ = RefreshHyperVFeatureStatusAsync();
        return card;
    }

    private StackPanel BuildVirtualMachineManagementPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(SectionTitle("虚拟机环境与硬件"));
        panel.Children.Add(Description("选择虚拟机管理电源与硬件；一键安装位于页面上方。"));
        _hyperVVmList = new ListBox { MinHeight = 70, MaxHeight = 150, Margin = new Thickness(0, 8, 0, 4), DisplayMemberPath = nameof(HyperVVirtualMachine.DisplayStatus), Style = (Style)FindResource("ToolWindowListBoxStyle") };
        _hyperVVmList.SelectionChanged += (_, _) =>
        {
            RefreshGuestCredentialSummary((_hyperVVmList.SelectedItem as HyperVVirtualMachine)?.Id);
            if (_hyperVVmList.SelectedItem is HyperVVirtualMachine selected && _resourceVmId != selected.Id)
            {
                _resourceVmId = selected.Id;
                _vmSelectedCpu.Text = selected.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _vmSelectedMemory.Text = selected.MemoryStartupMiB.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        };
        panel.Children.Add(_hyperVVmList);
        _hyperVVmStatus = Description("尚未读取虚拟机列表。");
        _ = RefreshVmSwitchesAsync();
        panel.Children.Add(_hyperVVmStatus);
        panel.Children.Add(ButtonRow(
            Button("刷新虚拟机", () => _ = RefreshHyperVVmsAsync()),
            Button("启动", () => _ = ChangeSelectedHyperVVmAsync(true)),
            Button("关机", () => _ = ChangeSelectedHyperVVmAsync(false)),
            Button("打开控制台", OpenSelectedHyperVConsole),
            Button("原生控制台", () => _ = OpenNativeHyperVConsoleAsync()),
            Button("删除", () => _ = DeleteSelectedHyperVVmAsync())));
        panel.Children.Add(BuildSelectedVmResources());
        panel.Children.Add(ButtonRow(
            Button("从 ISO 创建空白虚拟机（手动安装）", () => _ = CreateHyperVVmAsync()),
            Button("从 VHDX 模板创建", () => _ = CreateHyperVVmAsync(true)),
            Button("导出来宾初始化包", () => _ = ExportGuestInstallerAsync()),
            Button("测试 Agent 配对", () => _ = TestGuestPairingAsync()),
            Button("注册来宾通信通道", () => _ = RegisterGuestChannelAsync())));
        _ = RefreshHyperVVmsAsync();
        return panel;
    }

    private async Task RefreshHyperVVmsAsync()
    {
        if (_vmRefreshRunning || _guestMaintenanceBusy) return;
        _vmRefreshRunning = true;
        try
        {
            var selected = (_hyperVVmList?.SelectedItem as HyperVVirtualMachine)?.Id;
            var values = await _hyperVVmManager.ListAsync(CancellationToken.None);
            ApplyDesktopVmStates(values);
            if (_hyperVVmList is not null)
            {
                _hyperVVmList.ItemsSource = values;
                _hyperVVmList.SelectedItem = values.FirstOrDefault(vm => vm.Id == selected);
                if (_hyperVVmList.SelectedItem is null) SynchronizeSelectedVirtualMachine();
                if (!_vmPollRefresh) _hyperVVmStatus.Text = values.Count == 0 ? "未发现 Hyper-V 虚拟机。" : $"已发现 {values.Count} 台虚拟机，状态每 3 秒更新。";
            }
        }
        catch (Exception exception)
        {
            // A failed read used to leave the previous snapshot in place, so a machine the user had
            // already stopped kept its green running dot for the rest of the session. An unknown
            // state is reported as unknown instead.
            ClearDesktopVmStates();
            if (_hyperVVmList is not null) _hyperVVmStatus.Text = $"读取虚拟机失败：{exception.Message}";
        }
        finally { _vmRefreshRunning = false; }
    }

    private void ApplyDesktopVmStates(IReadOnlyList<HyperVVirtualMachine> values)
    {
        _desktopVmStates.Clear();
        foreach (var machine in values) _desktopVmStates[machine.Id] = machine.State;
        _desktopVmStatesObservedAt = DateTimeOffset.UtcNow;
        RefreshEnvironmentStateIndicators();
    }

    private void ClearDesktopVmStates()
    {
        if (_desktopVmStates.Count == 0) return;
        _desktopVmStates.Clear();
        _desktopVmStatesObservedAt = DateTimeOffset.MinValue;
        RefreshEnvironmentStateIndicators();
    }

    private async Task CreateHyperVVmAsync(bool fromTemplate = false)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = fromTemplate ? "选择已关闭、独立的 Windows VHDX 模板" : "选择 Windows ISO",
            Filter = fromTemplate ? "独立虚拟磁盘|*.vhdx" : "Windows ISO|*.iso"
        };
        if (dialog.ShowDialog(this) != true) return;
        var name = "月语分身-" + DateTime.Now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture);
        var environmentId = Guid.NewGuid();
        try
        {
            var options = ReadHyperVCreationFields();
            var root = Path.Combine(options.StorageDirectory, name);
            _hyperVVmStatus.Text = "正在创建第二代虚拟机，请勿关闭设置…";
            var vm = await _hyperVVmManager.CreateAsync(new HyperVVmCreateRequest(environmentId, name, root, fromTemplate ? string.Empty : dialog.FileName, options.CpuCount, options.MemoryStartupMiB, options.DiskSizeGiB, options.SwitchName, options.SecureBoot)
            {
                TemplatePath = fromTemplate ? dialog.FileName : null
            }, CancellationToken.None);
            _viewModel.AddDesktopSessionEnvironment(new EnvironmentProfile(environmentId, name, DesktopSessionBackendKind.HyperV, vm.Id.ToString(), SessionStartupPolicy.EnvironmentDefault, new SessionLimits(), []));
            RefreshDesktopSessionsPage();
            SetDesktopSessionStatus($"虚拟机已创建：{vm.Name}（{vm.Id}）。下一步需要安装来宾 Agent。", true);
            await RefreshHyperVVmsAsync();
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"创建失败：{exception.Message}"; }
    }

    private async Task ChangeSelectedHyperVVmAsync(bool start)
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm) { _hyperVVmStatus.Text = "请先选择虚拟机。"; return; }
        try
        {
            if (start) await _hyperVVmManager.StartAsync(vm.Id, CancellationToken.None);
            else await _hyperVVmManager.StopAsync(vm.Id, CancellationToken.None);
            await RefreshHyperVVmsAsync();
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"操作失败：{exception.Message}"; }
    }

    private async Task DeleteSelectedHyperVVmAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        {
            _hyperVVmStatus.Text = "请先选择虚拟机。";
            return;
        }

        var owned = _viewModel.DesktopSessionEnvironments.Any(environment =>
            environment.Backend == DesktopSessionBackendKind.HyperV &&
            Guid.TryParse(environment.BackendProfileId, out var id) && id == vm.Id) || vm.OwnedByProduct;
        if (!owned)
        {
            _hyperVVmStatus.Text = "为避免误删外部虚拟机，只能删除由月语创建并登记的虚拟机。";
            return;
        }

        if (vm.State != HyperVVirtualMachineState.Off)
        {
            _hyperVVmStatus.Text = "请先关机，再删除虚拟机。";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"确定删除虚拟机“{vm.Name}”及其虚拟磁盘吗？此操作不可撤销。",
            "删除 Hyper-V 虚拟机",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
            return;

        try
        {
            _hyperVVmStatus.Text = "正在删除虚拟机及虚拟磁盘，请稍候…";
            await _hyperVVmManager.DeleteAsync(vm.Id, deleteDisk: true, ownedByProduct: true, CancellationToken.None);
            var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault(item =>
                item.Backend == DesktopSessionBackendKind.HyperV &&
                item.BackendProfileId.Equals(vm.Id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (environment is not null)
                _viewModel.RemoveDesktopSessionEnvironment(environment.Id);
            RefreshDesktopSessionsPage();
            _hyperVVmStatus.Text = "虚拟机及虚拟磁盘已删除。";
            await RefreshHyperVVmsAsync();
        }
        catch (Exception exception)
        {
            _hyperVVmStatus.Text = $"删除失败：{exception.Message}";
        }
    }

    private async void OpenSelectedHyperVConsole()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        {
            _hyperVVmStatus.Text = "请先选择虚拟机。";
            return;
        }

        try
        {
            var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault(item =>
                item.Backend == DesktopSessionBackendKind.HyperV && Guid.TryParse(item.BackendProfileId, out var id) && id == vm.Id);
            if (environment is not null && _desktopSessionActions is not null)
            {
                var status = await _desktopSessionActions.StartEnvironmentAsync(environment.Id, CancellationToken.None);
                _hyperVVmStatus.Text = status.Message;
                return;
            }
            if (_standaloneHyperVConsole is null)
            {
                _standaloneHyperVConsole = new FloatDesk.App.DesktopSessions.HyperVConsoleController();
                Closed += (_, _) => _standaloneHyperVConsole.Dispose();
            }
            await _standaloneHyperVConsole.ShowAsync(vm.Id, 1280, 720, [],
                () => Task.FromResult("控制台已关闭，虚拟机继续运行。"), CancellationToken.None,
                openSettings: () => { Show(); Activate(); SelectDesktopSessionsPage(); });
            _hyperVVmStatus.Text = "已打开月语 Hyper-V 控制台。";
        }
        catch (Exception exception)
        {
            _hyperVVmStatus.Text = $"打开 Hyper-V 控制台失败：{exception.Message}";
        }
    }

    private FloatDesk.App.DesktopSessions.HyperVConsoleController? _standaloneHyperVConsole;
    private FloatDesk.App.DesktopSessions.HyperVConsoleController? _nativeHyperVConsole;

    private async Task OpenNativeHyperVConsoleAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm) { _hyperVVmStatus.Text = "请先选择虚拟机。"; return; }
        try
        {
            if (_nativeHyperVConsole is null)
            {
                _nativeHyperVConsole = new FloatDesk.App.DesktopSessions.HyperVConsoleController();
                Closed += (_, _) => _nativeHyperVConsole.Dispose();
            }
            await _nativeHyperVConsole.ShowAsync(vm.Id, 1280, 720, [], () => Task.FromResult("控制台已关闭，虚拟机继续运行。"), CancellationToken.None,
                openSettings: () => { Show(); Activate(); SelectDesktopSessionsPage(); }, useSavedLogin: false);
            _hyperVVmStatus.Text = "已打开原生连接，不使用自动认证。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = exception.Message; }
    }

    private void OpenWindowsFeatures()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OptionalFeatures.exe"),
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            SetDesktopSessionStatus($"无法打开 Windows 功能：{exception.Message}", false);
        }
    }

    private async Task ChangeHyperVFeatureAsync(bool enable)
    {
        if (_hyperVFeatureOperationRunning)
            return;
        var message = enable
            ? "将安装/启用 Hyper-V 系统组件，可能改变其他虚拟机、模拟器的运行方式。需要管理员授权，可能需要重启；不会创建 VM 或自动重启。继续？"
            : "将关闭 Hyper-V 系统组件。请先保存并关闭所有 Hyper-V 虚拟机；此操作影响本机所有 Hyper-V 用户，可能需要重启，不删除虚拟机或磁盘。继续？";
        if (MessageBox.Show(this, message, "Hyper-V 系统组件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _hyperVFeatureOperationRunning = true;
        _hyperVFeatureStatus.Text = "正在处理系统组件，请等待完成后再关闭设置。";
        try
        {
            using var process = Process.Start(HyperVFeatureManagement.CreateCommand(enable))
                ?? throw new InvalidOperationException("无法启动 Windows 组件管理器。");
            await process.WaitForExitAsync();
            var result = HyperVFeatureManagement.ClassifyExitCode(process.ExitCode);
            if (!result.Succeeded)
            {
                _hyperVFeatureStatus.Text =
                    $"系统组件操作失败（退出码 {result.ExitCode}），请查看 Windows 的 DISM 日志。";
                return;
            }

            if (enable)
            {
                using var toolsProcess = Process.Start(
                    HyperVFeatureManagement.CreateManagementToolsCommand())
                    ?? throw new InvalidOperationException("无法安装 Hyper-V 管理工具。");
                await toolsProcess.WaitForExitAsync();
                var toolsResult = HyperVFeatureManagement.ClassifyExitCode(toolsProcess.ExitCode);
                if (!toolsResult.Succeeded)
                {
                    _hyperVFeatureStatus.Text =
                        $"Hyper-V 组件已处理，但管理工具安装失败（退出码 {toolsResult.ExitCode}）。";
                    return;
                }
            }

            using var bootProcess = Process.Start(
                HyperVFeatureManagement.CreateBootConfigurationCommand(enable))
                ?? throw new InvalidOperationException("无法配置 Hyper-V 启动项。");
            await bootProcess.WaitForExitAsync();
            if (bootProcess.ExitCode != 0)
            {
                _hyperVFeatureStatus.Text =
                    $"系统组件已处理，但 Hyper-V 启动配置失败（退出码 {bootProcess.ExitCode}）。请以管理员身份运行 bcdedit。";
                return;
            }

            await RefreshHyperVFeatureStatusAsync();
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _hyperVFeatureStatus.Text = "已取消管理员授权，未启动组件操作。";
        }
        catch (Exception exception)
        {
            _hyperVFeatureStatus.Text = $"系统组件操作失败：{exception.Message}";
        }
        finally
        {
            _hyperVFeatureOperationRunning = false;
        }
    }

    private async Task RefreshHyperVFeatureStatusAsync()
    {
        if (_hyperVFeatureStatus is null)
            return;
        try
        {
            var status = await HyperVFeatureManagement.ReadStatusAsync();
            _hyperVFeatureSnapshot = status;
            _hyperVFeatureStatus.Text = status switch
            {
                { FeatureEnabled: true, HypervisorLaunchEnabled: true, HypervisorRunning: true } =>
                    status.ManagementToolsAvailable
                        ? "Hyper-V 宿主已运行。还需要在设置中填写现有 Windows 虚拟机 GUID，并完成来宾 Agent 配对，才能启动虚拟机分身。"
                        : "Hyper-V 宿主已运行，但未发现 Hyper-V PowerShell 管理模块；请在 Windows 功能中启用 Hyper-V 管理工具后重启。",
                { FeatureEnabled: true, HypervisorLaunchEnabled: true, HypervisorRunning: false } =>
                    "Hyper-V 组件和启动项已配置，但本次启动尚未加载 Hyper-V；请重启电脑后重新打开设置。Hyper-V PowerShell 模块也会在重启后重新检测。",
                { FeatureEnabled: true, HypervisorLaunchEnabled: false } =>
                    "Hyper-V 组件已安装，但启动项仍为 Off；点击“安装/启用 Hyper-V”修复，随后重启电脑。",
                { FeatureEnabled: false } =>
                    "Hyper-V 组件未启用。点击“安装/启用 Hyper-V”并按提示授权，随后重启电脑。",
                _ => string.IsNullOrWhiteSpace(status.Detail)
                    ? "无法确认 Hyper-V 系统状态，请打开 Windows 功能检查。"
                    : $"无法确认 Hyper-V 系统状态：{status.Detail}"
            };
            RefreshDesktopDiagnostics();
        }
        catch (Exception exception)
        {
            _hyperVFeatureSnapshot = null;
            _hyperVFeatureStatus.Text = $"读取 Hyper-V 状态失败：{exception.Message}";
            RefreshDesktopDiagnostics();
        }
    }
}
