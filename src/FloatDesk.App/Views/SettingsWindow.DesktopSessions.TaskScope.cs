using FloatDesk.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private ComboBox _desktopTaskScope = null!;
    private Expander _desktopTaskManualPanel = null!;
    private readonly Dictionary<int, Guid> _desktopTaskScopes = [];
    private bool _refreshingTaskScope;

    private void OnDesktopTaskScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingTaskScope || !_desktopSessionsUiReady) return;
        if (_desktopTaskScope.SelectedItem is EnvironmentProfile environment)
            _desktopTaskScopes[_desktopTaskBackendTabs.SelectedIndex] = environment.Id;
        _selectedDesktopTaskId = null;
        _selectedDesktopStepId = null;
        RefreshDesktopTaskScope();
        RefreshDesktopSteps();
    }

    private void RefreshDesktopTaskScope()
    {
        if (_refreshingTaskScope) return;
        _refreshingTaskScope = true;
        try
        {
            var environments = SelectedTaskGlobal ? new[] {GlobalTaskTarget.Profile} : _viewModel.DesktopSessionEnvironments.Where(env => env.Backend == SelectedTaskBackend).ToArray();
            var selectedId = _desktopTaskScopes.GetValueOrDefault(_desktopTaskBackendTabs.SelectedIndex);
            var environment = environments.FirstOrDefault(env => env.Id == selectedId) ?? environments.FirstOrDefault();
            _desktopTaskScope.ItemsSource = environments;
            _desktopTaskScope.SelectedItem = environment;
            _desktopTaskScope.IsEnabled = environments.Length > 0;
            if (environment is not null) _desktopTaskScopes[_desktopTaskBackendTabs.SelectedIndex] = environment.Id;
            // A task can only target the environment currently being edited.
            _desktopTaskEnvironment.ItemsSource = environment is null ? Array.Empty<EnvironmentProfile>() : new[] { environment };
            _desktopTaskEnvironment.SelectedItem = environment;
            var selectedTaskId = _selectedDesktopTaskId;
            var tasks = _viewModel.DesktopSessionTasks.Where(task => environment is not null && task.EnvironmentId == environment.Id).ToArray();
            _desktopTaskList.ItemsSource = tasks;
            _desktopTaskList.SelectedItem = tasks.FirstOrDefault(task => task.Id == selectedTaskId) ?? tasks.FirstOrDefault();
            if (_desktopTaskList.SelectedItem is null)
            {
                _selectedDesktopTaskId = null;
                _selectedDesktopStepId = null;
                RefreshTaskPackageEditor(null);
            }
        }
        finally { _refreshingTaskScope = false; }
    }

    private DataTemplate BuildTaskItemTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        var toggle = new FrameworkElementFactory(typeof(CheckBox));
        toggle.SetValue(DockPanel.DockProperty, Dock.Right);
        toggle.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 4, 0));
        toggle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        toggle.SetValue(FrameworkElement.StyleProperty, FindResource("ToggleSwitchStyle"));
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(DesktopSessionTask.Enabled)) { Mode = BindingMode.OneWay });
        toggle.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(DesktopSessionTask.Name)) { StringFormat = "开启／关闭任务：{0}" });
        toggle.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(nameof(DesktopSessionTask.Name)) { StringFormat = "开启／关闭任务：{0}" });
        toggle.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnTaskRowToggle));
        row.AppendChild(toggle);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(DesktopSessionTask.Name)));
        name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        row.AppendChild(name);
        return new DataTemplate(typeof(DesktopSessionTask)) { VisualTree = row };
    }

    private void OnTaskRowToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: DesktopSessionTask task } toggle ||
            _desktopTaskScope.SelectedItem is not EnvironmentProfile environment || task.EnvironmentId != environment.Id) return;
        var enabled = toggle.IsChecked == true;
        _viewModel.UpdateDesktopSessionTask(task.Id, item =>
        {
            var next=item with {Enabled=enabled,Revision=checked(item.Revision+1),McpApprovalHash=null,
                McpEnvironmentBinding=DesktopTaskPackagePolicy.EnvironmentBinding(environment)};
            return next with {McpApprovalHash=DesktopTaskPackagePolicy.DefinitionHash(next)};
        });
        RefreshDesktopTaskScope();
        SetDesktopSessionStatus($"任务已{(enabled ? "开启" : "关闭")}，点击“应用”后按触发方式生效；手动任务开启后执行一次。关闭不会中断已开始的运行。");
        e.Handled = true;
    }
}
