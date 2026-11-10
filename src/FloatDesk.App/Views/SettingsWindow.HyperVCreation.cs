using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TextBox _vmStorage = null!;
    private TextBox _vmCpu = null!;
    private TextBox _vmMemory = null!;
    private TextBox _vmDisk = null!;
    private ComboBox _vmSwitch = null!;
    private CheckBox _vmCreateGpu = null!;
    private TextBox _vmSelectedCpu = null!;
    private TextBox _vmSelectedMemory = null!;
    private ComboBox _vmSelectedSwitch = null!;
    private Guid? _resourceVmId;
    private readonly HyperVResourceManager _vmResources = new();

    private StackPanel BuildHyperVCreationFields()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var defaults = new HyperVAutomationSettings();
        _vmStorage = AddVmInput(panel, "安装目录（每台虚拟机单独创建子目录）",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YueYuOrb", "HyperV"));
        panel.Children.Add(Button("选择安装目录", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择虚拟机安装目录" };
            if (dialog.ShowDialog(this) == true)
                _vmStorage.Text = dialog.FolderName;
        }));
        _vmCpu = AddVmInput(panel, "CPU 数量", defaults.CpuCount.ToString(CultureInfo.InvariantCulture));
        _vmMemory = AddVmInput(panel, "启动内存（MiB）", defaults.MemoryStartupMiB.ToString(CultureInfo.InvariantCulture));
        _vmDisk = AddVmInput(panel, "动态磁盘最大容量（GiB）", defaults.DiskSizeGiB.ToString(CultureInfo.InvariantCulture));
        panel.Children.Add(Description("网络交换机"));
        _vmSwitch = new ComboBox { IsEditable = false, DisplayMemberPath = nameof(HyperVSwitch.Name),
            Style = (Style)FindResource("SettingsComboBoxStyle") };
        panel.Children.Add(_vmSwitch);
        panel.Children.Add(Button("刷新网络交换机", () => _ = RefreshVmSwitchesAsync()));
        _vmCreateGpu = new CheckBox { Content = "自动安装后开启主机显卡共享（GPU-P，实验性）",
            Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 8, 0, 6) };
        panel.Children.Add(_vmCreateGpu);
        panel.Children.Add(Description("以上配置用于下一次创建。动态磁盘按使用量增长；交换机须已存在。"));
        return panel;
    }

    private TextBox AddVmInput(Panel panel, string label, string value)
    {
        panel.Children.Add(Description(label));
        var input = new TextBox
        {
            Text = value, Margin = new Thickness(0, 3, 0, 8),
            Style = (Style)FindResource("SettingsTextBoxStyle")
        };
        panel.Children.Add(input);
        return input;
    }

    private HyperVAutomationSettings ReadHyperVCreationFields()
    {
        if (!int.TryParse(_vmCpu.Text, out var cpu) ||
            !long.TryParse(_vmMemory.Text, out var memory) ||
            !long.TryParse(_vmDisk.Text, out var disk))
            throw new InvalidOperationException("CPU、内存和磁盘容量必须为整数。");
        var settings = new HyperVAutomationSettings
        {
            StorageDirectory = _vmStorage.Text.Trim(), CpuCount = cpu,
            MemoryStartupMiB = memory, MemoryMaximumMiB = memory,
            DiskSizeGiB = disk, SwitchName = (_vmSwitch.SelectedItem as HyperVSwitch)?.Name ?? string.Empty,
            GpuMode = _vmCreateGpu.IsChecked == true ? HyperVGpuMode.GpuPartition : HyperVGpuMode.VirtualDisplay
        };
        if (settings.Validate() is { } error)
            throw new InvalidOperationException(error);
        return settings;
    }

    private async Task RefreshVmSwitchesAsync()
    {
        try
        {
            var previous = (_vmSwitch.SelectedItem as HyperVSwitch)?.Name;
            var switches = await _vmResources.ListSwitchesAsync(CancellationToken.None);
            _vmSwitch.ItemsSource = switches;
            _vmSwitch.SelectedItem = switches.FirstOrDefault(item => item.Name == previous)
                ?? switches.FirstOrDefault(item => item.Name == "Default Switch") ?? (switches.Count > 0 ? switches[0] : null);
            var selectedSwitch = (_vmSelectedSwitch.SelectedItem as HyperVSwitch)?.Name;
            _vmSelectedSwitch.ItemsSource = switches;
            _vmSelectedSwitch.SelectedItem = switches.FirstOrDefault(item => item.Name == selectedSwitch)
                ?? switches.FirstOrDefault(item => item.Name == "Default Switch");
            _hyperVVmStatus.Text = switches.Count == 0 ? "未发现交换机，请先在 Hyper-V 管理器中配置网络。" : $"已读取 {switches.Count} 个交换机；内部交换机需要 NAT/DHCP 才能联网。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"读取交换机失败：{exception.Message}"; }
    }

    private async Task ApplyVmResourcesAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        { _hyperVVmStatus.Text = "请先选择虚拟机。"; return; }
        if (vm.State != HyperVVirtualMachineState.Off)
        { _hyperVVmStatus.Text = "请先正常关闭来宾系统，再修改 CPU 和启动内存。"; return; }
        if (!int.TryParse(_vmSelectedCpu.Text, out var cpu) || !long.TryParse(_vmSelectedMemory.Text, out var memory))
        { _hyperVVmStatus.Text = "CPU 和内存必须为整数。"; return; }
        try
        {
            var actual = await _vmResources.ApplyAsync(vm.Id, cpu, memory, CancellationToken.None);
            await RefreshHyperVVmsAsync();
            _hyperVVmStatus.Text = $"已验证实际配置：{actual.CpuCount} CPU，{(actual.FixedMemoryForGpu ? "固定内存" : "启动内存")} {actual.MemoryStartupMiB} MiB。" +
                (actual.FixedMemoryForGpu ? "已自动应用显卡共享兼容的固定内存。" : "") + "磁盘及网络未更改。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"修改资源失败：{exception.Message}"; }
    }

    private async Task ConnectSelectedVmNetworkAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm ||
            _vmSelectedSwitch.SelectedItem is not HyperVSwitch selected)
        { _hyperVVmStatus.Text = "请选择虚拟机和网络交换机。"; return; }
        try
        {
            await _vmResources.ConnectNetworkAsync(vm.Id, selected.Name, CancellationToken.None);
            _hyperVVmStatus.Text = $"已验证虚拟网卡连接到 {selected.Name}。如来宾仍不能联网，请检查来宾 DHCP、网关和 DNS；内部交换机本身不提供互联网。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"连接交换机失败：{exception.Message}"; }
    }

    private bool _vmGpuBusy;

    private async Task ToggleEnvironmentGpuAsync()
    {
        if (_vmGpuBusy) return;
        var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault(item => item.Id == _selectedDesktopEnvironmentId);
        if (environment is null || environment.Backend != DesktopSessionBackendKind.HyperV ||
            !Guid.TryParse(environment.BackendProfileId, out var vmId))
        { _desktopEnvironmentGpu.IsChecked = false; SetDesktopSessionStatus("请先选择有效的虚拟机环境。", false); return; }
        var enabled = _desktopEnvironmentGpu.IsChecked == true;
        var previousEnabled = !enabled;
        ++_gpuReadVersion;
        _vmGpuBusy = true;
        _desktopEnvironmentGpu.IsEnabled = false;
        _desktopEnvironmentList.IsEnabled = false;
        _desktopEnvironmentFilter.IsEnabled = false;
        SetDesktopSessionStatus(enabled ? "正在检查 GPU-P 并部署驱动，请勿启动虚拟机…" : "正在关闭显卡共享并恢复配置…");
        try
        {
            await HyperVGpuSharing.ApplyAsync(vmId, enabled, CancellationToken.None);
            _viewModel.UpdateDesktopSessionEnvironment(environment.Id, current => current with { GpuSharingEnabled = enabled });
            var saved = await _settings.UpdateAsync(current => current with
            {
                DesktopSessions = current.DesktopSessions with { Environments = current.DesktopSessions.Environments
                    .Select(item => item.Id == environment.Id ? item with { GpuSharingEnabled = enabled } : item).ToArray() }
            }, CancellationToken.None);
            SetDesktopSessionStatus(saved.Success
                ? (enabled ? "显卡共享已配置并保存；启动来宾后检查实际 GPU 加速。" : "显卡共享已关闭并保存。")
                : "显卡配置已生效，但偏好保存失败，请点击应用重试。", saved.Success);
        }
        catch (Exception exception)
        {
            _desktopEnvironmentGpu.IsChecked = previousEnabled;
            SetDesktopSessionStatus($"显卡共享未完成：{exception.Message}", false);
        }
        finally
        {
            _vmGpuBusy = false;
            _desktopEnvironmentGpu.IsEnabled = true;
            _desktopEnvironmentList.IsEnabled = true;
            _desktopEnvironmentFilter.IsEnabled = true;
        }
    }

    private int _gpuReadVersion;
    private async Task RefreshEnvironmentGpuStateAsync(Guid environmentId, Guid vmId)
    {
        var version = ++_gpuReadVersion;
        try
        {
            var actual = await HyperVGpuSharing.IsEnabledAsync(vmId, CancellationToken.None);
            if (version == _gpuReadVersion && !_vmGpuBusy && _selectedDesktopEnvironmentId == environmentId)
                _desktopEnvironmentGpu.IsChecked = actual;
        }
        catch (Exception exception)
        {
            if (version == _gpuReadVersion && !_vmGpuBusy && _selectedDesktopEnvironmentId == environmentId)
                SetDesktopSessionStatus($"显卡共享状态未能读取：{exception.Message}", false);
        }
    }

    private StackPanel BuildSelectedVmResources()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _vmSelectedCpu = AddVmInput(panel, "选中虚拟机 CPU 数量", "4");
        _vmSelectedMemory = AddVmInput(panel, "选中虚拟机启动内存（MiB）", "4096");
        panel.Children.Add(Description("虚拟机网络交换机"));
        _vmSelectedSwitch = new ComboBox { DisplayMemberPath = nameof(HyperVSwitch.Name), Style = (Style)FindResource("SettingsComboBoxStyle") };
        panel.Children.Add(_vmSelectedSwitch);
        panel.Children.Add(ButtonRow(
            Button("应用 CPU／内存（需关机）", () => _ = ApplyVmResourcesAsync()),
            Button("应用网络交换机", () => _ = ConnectSelectedVmNetworkAsync())));
        panel.Children.Add(Description("显卡共享、分辨率、关闭行为与共享文件夹在上方对应的虚拟机环境中设置。"));
        return panel;
    }
}
