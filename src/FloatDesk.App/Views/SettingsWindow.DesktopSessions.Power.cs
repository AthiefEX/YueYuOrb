using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private readonly Dictionary<Guid, HyperVVirtualMachineState> _desktopVmStates = [];
    private bool _environmentPowerBusy;
    private DateTimeOffset _desktopVmStatesObservedAt = DateTimeOffset.MinValue;

    // FrameworkElementFactory's name argument only registers an x:Name in the template's name
    // scope; it never assigns FrameworkElement.Name. Matching the dot by Name therefore found
    // nothing, so the environment list kept whatever color it was painted with the first time the
    // page was shown, and a closed machine stayed green for the rest of the session.
    private static readonly DependencyProperty IsEnvironmentStateIndicatorProperty =
        DependencyProperty.RegisterAttached(
            "IsEnvironmentStateIndicator",
            typeof(bool),
            typeof(SettingsWindow),
            new PropertyMetadata(false));

    private DataTemplate BuildEnvironmentItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        // The dot is identified by IsEnvironmentStateIndicator, never by name: a template name is
        // not copied onto FrameworkElement.Name, so name matching in the visual tree silently fails.
        var indicator = new FrameworkElementFactory(typeof(Ellipse));
        indicator.SetValue(IsEnvironmentStateIndicatorProperty, true);
        indicator.SetValue(DockPanel.DockProperty, Dock.Right);
        indicator.SetValue(FrameworkElement.WidthProperty, 10.0);
        indicator.SetValue(FrameworkElement.HeightProperty, 10.0);
        indicator.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 4, 0));
        indicator.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        indicator.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => UpdateEnvironmentStateIndicator((Ellipse)sender)));
        row.AppendChild(indicator);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(EnvironmentProfile.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        row.AppendChild(name);
        return new DataTemplate(typeof(EnvironmentProfile)) { VisualTree = row };
    }

    private void UpdateEnvironmentStateIndicator(Ellipse indicator)
    {
        if (indicator.DataContext is not EnvironmentProfile environment || environment.Backend != DesktopSessionBackendKind.HyperV)
        { indicator.Visibility = Visibility.Collapsed; return; }
        indicator.Visibility = Visibility.Visible;
        var knownState = HyperVVirtualMachineState.Unknown;
        var known = Guid.TryParse(environment.BackendProfileId, out var id) &&
            _desktopVmStates.TryGetValue(id, out knownState);
        var badge = DesktopVmStateBadges.For(known, knownState, DateTimeOffset.UtcNow - _desktopVmStatesObservedAt);
        indicator.Fill = new SolidColorBrush(Color.FromArgb(
            (byte)(badge.Argb >> 24), (byte)(badge.Argb >> 16), (byte)(badge.Argb >> 8), (byte)badge.Argb));
        indicator.ToolTip = badge.Text;
        System.Windows.Automation.AutomationProperties.SetName(indicator, badge.Text);
    }

    private void RememberDesktopVmState(Guid vmId, HyperVVirtualMachineState state)
    {
        _desktopVmStates[vmId] = state;
        _desktopVmStatesObservedAt = DateTimeOffset.UtcNow;
    }

    private void RefreshEnvironmentStateIndicators()
    {
        if (_desktopEnvironmentList is null) return;
        void Visit(DependencyObject element)
        {
            if (element is Ellipse dot && (bool)dot.GetValue(IsEnvironmentStateIndicatorProperty))
                UpdateEnvironmentStateIndicator(dot);
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) Visit(VisualTreeHelper.GetChild(element, index));
        }
        Visit(_desktopEnvironmentList);
    }

    private async Task RunEnvironmentPowerAsync(string operation)
    {
        if (_environmentPowerBusy || _guestMaintenanceBusy || _vmGpuBusy)
        {
            // Silence here is indistinguishable from a broken button; say why nothing happened.
            SetDesktopSessionStatus("另一项虚拟机操作尚未完成，请稍候再试。", false);
            return;
        }
        if (_desktopEnvironmentList.SelectedItem is not EnvironmentProfile { Backend: DesktopSessionBackendKind.HyperV } environment ||
            !Guid.TryParse(environment.BackendProfileId, out var vmId))
        { SetDesktopSessionStatus("请先选择有效的虚拟机环境。", false); return; }
        _environmentPowerBusy = true;
        _desktopVmActionRow.IsEnabled = _desktopEnvironmentList.IsEnabled = _desktopEnvironmentFilter.IsEnabled = false;
        _desktopHyperVOptions.IsEnabled = false;
        try
        {
            SetDesktopSessionStatus(operation == "save" ? "正在保存状态并释放运行资源…" : "正在执行虚拟机电源操作…");
            if (operation == "save")
            {
                await _hyperVVmManager.ApplyCloseActionAsync(vmId, HyperVConsoleCloseAction.SaveState, CancellationToken.None);
                RememberDesktopVmState(vmId, HyperVVirtualMachineState.Saved);
            }
            else if (operation == "start")
            {
                await _hyperVVmManager.StartAsync(vmId, CancellationToken.None);
                RememberDesktopVmState(vmId, HyperVVirtualMachineState.Running);
            }
            else if (operation == "shutdown")
            {
                await _hyperVVmManager.StopAsync(vmId, CancellationToken.None);
                RememberDesktopVmState(vmId, HyperVVirtualMachineState.Off);
            }
            else throw new ArgumentException("Unknown power operation.", nameof(operation));
            RefreshEnvironmentStateIndicators();
            await RefreshHyperVVmsAsync();
            SetDesktopSessionStatus(operation == "save"
                ? "已保存虚拟机状态并释放运行资源。"
                : "电源操作完成，虚拟机状态已刷新。");
        }
        catch (Exception exception) { SetDesktopSessionStatus("电源操作未完成：" + exception.Message, false); }
        finally { _environmentPowerBusy = false; _desktopVmActionRow.IsEnabled = _desktopEnvironmentList.IsEnabled = _desktopEnvironmentFilter.IsEnabled = _desktopHyperVOptions.IsEnabled = true; }
    }
}
