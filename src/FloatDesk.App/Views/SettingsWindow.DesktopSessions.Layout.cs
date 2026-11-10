using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private Popup _desktopAdvancedPopup = null!;
    private WrapPanel _desktopVmActionRow = null!;
    private Border BuildDesktopAdvancedPanel()
    {
        _desktopAdvancedPanel = Card();
        _desktopAdvancedPanel.Visibility = Visibility.Collapsed;
        _desktopAdvancedPanel.Margin = new Thickness(0);
        _desktopAdvancedTabs = new TabControl { Style = (Style)FindResource("SettingsTabControlStyle") };
        void AddTab(string title, UIElement content) => _desktopAdvancedTabs.Items.Add(new TabItem
        {
            Header = title, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 4, 8, 0) },
            Style = (Style)FindResource("NavigationTabItemStyle")
        });
        AddTab("环境与硬件", BuildHyperVManagementCard());
        AddTab("宿主磁盘共享", _desktopSharingPanel);
        AddTab("网络与自动登录", BuildGuestMaintenancePanel());
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = Button("关闭", () => _desktopAdvancedPopup.IsOpen = false);
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(SectionTitle("虚拟机高级设置"));
        layout.Children.Add(header);
        Grid.SetRow(_desktopAdvancedTabs, 1); layout.Children.Add(_desktopAdvancedTabs);
        _desktopAdvancedPanel.Child = layout;
        _desktopAdvancedPopup = new Popup { Child = _desktopAdvancedPanel, Placement = PlacementMode.Center,
            PlacementTarget = this, AllowsTransparency = true, StaysOpen = false };
        _desktopAdvancedPopup.Closed += (_, _) => _desktopAdvancedPanel.Visibility = Visibility.Collapsed;
        Closed += (_, _) => _desktopAdvancedPopup.IsOpen = false;
        IsVisibleChanged += (_, _) => { if (!IsVisible) _desktopAdvancedPopup.IsOpen = false; };
        return _desktopAdvancedPanel;
    }

    private Expander FoldDesktopSection(string title, Border card)
    {
        // Transfer content once; it must never have two WPF parents.
        var content = card.Child;
        card.Child = null;
        if (content is StackPanel panel && panel.Children.Count > 0 &&
            panel.Children[0] is TextBlock heading && heading.Text == title)
            panel.Children.RemoveAt(0);
        var expander = new Expander
        {
            Header = title, Content = content, IsExpanded = false,
            Style = (Style)FindResource("DesktopSessionExpanderStyle"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        _desktopFoldedSections.Add(expander);
        return expander;
    }

    private void ToggleDesktopAdvancedPanel()
    {
        if (_desktopBackendKind.SelectedItem is not DesktopSessionBackendKind.HyperV) return;
        if (_desktopAdvancedPopup.IsOpen) { _desktopAdvancedPopup.IsOpen = false; return; }
        _desktopAdvancedPanel.Width = Math.Max(600, Math.Min(1040, ActualWidth - 48));
        _desktopAdvancedPanel.Height = Math.Max(360, Math.Min(700, ActualHeight - 80));
        _desktopAdvancedPanel.Visibility = Visibility.Visible;
        _desktopAdvancedPopup.IsOpen = true;
        if (_desktopAdvancedPopup.IsOpen)
        {
            SynchronizeSelectedVirtualMachine();
            _ = RefreshHyperVVmsAsync();
        }
    }

    private void UpdateDesktopEnvironmentContext()
    {
        if (_desktopLocalOptions is null || _desktopHyperVOptions is null) return;
        var virtualMachine = _desktopBackendKind.SelectedItem is DesktopSessionBackendKind.HyperV;
        _desktopLocalOptions.Visibility = virtualMachine ? Visibility.Collapsed : Visibility.Visible;
        _desktopHyperVOptions.Visibility = virtualMachine ? Visibility.Visible : Visibility.Collapsed;
        _desktopEnvironmentGpu.Visibility = virtualMachine ? Visibility.Visible : Visibility.Collapsed;
        _desktopAdvancedButton.Visibility = virtualMachine ? Visibility.Visible : Visibility.Collapsed;
        _desktopVmActionRow.Visibility = virtualMachine ? Visibility.Visible : Visibility.Collapsed;
        if (!virtualMachine && _desktopAdvancedPanel is not null)
        {
            _desktopAdvancedPopup.IsOpen = false;
            _desktopAdvancedPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SynchronizeSelectedVirtualMachine()
    {
        if (_hyperVVmList is null) return;
        if (!Guid.TryParse(_desktopBackendProfile.Text, out var vmId))
        { _hyperVVmList.SelectedItem = null; return; }
        var target = _hyperVVmList.Items.Cast<HyperVVirtualMachine>().FirstOrDefault(vm => vm.Id == vmId);
        // Clear a previous target while the new environment VM is missing or
        // still loading; never leave hardware actions bound to another VM.
        _hyperVVmList.SelectedItem = target;
    }
}
