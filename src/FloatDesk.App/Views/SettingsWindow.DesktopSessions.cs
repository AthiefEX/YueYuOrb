using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private static readonly string[] DesktopResolutionOptions =
        ["1024 × 768", "1280 × 720", "1600 × 900", "1920 × 1080", "2560 × 1440", "3840 × 2160"];
    private readonly Dictionary<string, TextBox> _stepParameterInputs =
        new(StringComparer.Ordinal);
    private ListBox _desktopEnvironmentList = null!;
    private ListBox _desktopTaskList = null!;
    private ListBox _desktopStartupList = null!;
    private ListBox _desktopStepList = null!;
    private TextBox _desktopEnvironmentName = null!;
    private TextBox _desktopBackendProfile = null!;
    private ComboBox _desktopBackendKind = null!;
    private ComboBox _desktopStartupPolicy = null!;
    private ComboBox _desktopResolution = null!;
    private ComboBox _desktopConsoleClose = null!;
    private CheckBox _desktopHostSharing = null!;
    private CheckBox _desktopEnvironmentGpu = null!;
    private CheckBox _desktopUseSavedLogin = null!;
    private StackPanel _desktopHyperVOptions = null!;
    private ComboBox _desktopEnvironmentFilter = null!;
    private Grid _desktopEnvironmentColumns = null!;
    private StackPanel _desktopLocalOptions = null!;
    private StackPanel _desktopSharingPanel = null!;
    private Border _desktopAdvancedPanel = null!;
    private TabControl _desktopAdvancedTabs = null!;
    private Button _desktopAdvancedButton = null!;
    private readonly List<Expander> _desktopFoldedSections = [];
    private TextBox _desktopHostSharedPaths = null!;
    private static readonly string[] HyperVCloseOptions =
    [
        "仅关闭控制台，虚拟机继续运行",
        "保存状态并释放运行资源，下次恢复",
        "直接断电关机（未保存内容会丢失），下次启动"
    ];
    private TextBox _desktopTaskName = null!;
    private ComboBox _desktopTaskEnvironment = null!;
    private ComboBox _desktopTaskTrigger = null!;
    private TextBox _desktopTaskTime = null!;
    private TextBox _desktopTaskTimeZone = null!;
    private CheckBox _desktopTaskEnabled = null!;
    private ComboBox _desktopStepKind = null!;
    private StackPanel _desktopStepParameters = null!;
    private TextBox _desktopStepTimeout = null!;
    private TextBox _desktopStepRetry = null!;
    private TextBox _startupPath = null!;
    private TextBox _startupArguments = null!;
    private TextBox _startupWorkingDirectory = null!;
    private TextBox _startupDelay = null!;
    private CheckBox _startupWaitForExit = null!;
    private CheckBox _startupEnabled = null!;
    private TextBlock _desktopSessionStatus = null!;
    private ListBox _desktopActivityList = null!;
    private StackPanel _desktopDiagnosticsRows = null!;
    private readonly DesktopSessionDiagnostics _desktopDiagnostics =
        new(AppContext.BaseDirectory);
    private HyperVFeatureStatus? _hyperVFeatureSnapshot;
    private bool _desktopSessionsUiReady;
    private Guid? _selectedDesktopEnvironmentId;
    private Guid? _selectedDesktopTaskId;
    private Guid? _selectedDesktopStepId;
    private Guid? _selectedStartupEntryId;

    private void InitializeDesktopSessionsSettings()
    {
        if (DesktopSessionsPage is null || _desktopSessionsUiReady)
            return;
        _desktopSessionsUiReady = true;
        var root = new StackPanel();
        DesktopSessionsPage.Children.Add(root);

        var header = Card();
        header.Margin = new Thickness(0);
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        var headerText = new StackPanel();
        headerText.Children.Add(SectionTitle("桌面分身"));
        headerText.Children.Add(Description(
            "默认关闭。启用的环境、自启动清单和任务保存在本地设置中，不会在应用启动时自动创建会话。"));
        headerGrid.Children.Add(headerText);
        var enabledToggle = new CheckBox
        {
            Content = "本机分身（无需虚拟机）",
            Style = (Style)FindResource("ToggleSwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0)
        };
        enabledToggle.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(ViewModels.SettingsViewModel.LocalDesktopSessionEnabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        Grid.SetColumn(enabledToggle, 1);
        var modeToggles = new StackPanel();
        modeToggles.Children.Add(enabledToggle);
        var vmToggle = new CheckBox
        {
            Content = "虚拟机分身（Hyper-V）",
            Style = (Style)FindResource("ToggleSwitchStyle"),
            Margin = new Thickness(20, 10, 0, 0)
        };
        vmToggle.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(ViewModels.SettingsViewModel.HyperVDesktopSessionEnabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        modeToggles.Children.Add(vmToggle);
        Grid.SetColumn(modeToggles, 1);
        headerGrid.Children.Add(modeToggles);
        header.Child = headerGrid;
        root.Children.Add(header);

        var installCard = Card();
        installCard.Margin = new Thickness(0, 14, 0, 0);
        var installation = new StackPanel();
        installation.Children.Add(SectionTitle("一键安装虚拟机"));
        installation.Children.Add(BuildHyperVCreationFields());
        installation.Children.Add(BuildAutomaticInstallFields());
        installCard.Child = installation;

        var columns = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };
        columns.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.Children.Add(BuildEnvironmentCard());
        var taskCard = BuildTaskCard();
        root.Children.Add(columns);
        _ = BuildDesktopAdvancedPanel();
        root.Children.Add(_desktopAdvancedPopup);
        root.Children.Add(FoldDesktopSection("一键安装虚拟机", installCard));
        taskCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(FoldDesktopSection("自动任务", taskCard));

        var diagnosticsCard = Card();
        diagnosticsCard.Margin = new Thickness(0, 14, 0, 0);
        var diagnosticsPanel = new StackPanel();
        var diagnosticsHeader = new Grid();
        diagnosticsHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        diagnosticsHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        diagnosticsHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        diagnosticsHeader.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        var diagnosticsHeading = new StackPanel();
        diagnosticsHeading.Children.Add(SectionTitle("诊断"));
        diagnosticsHeading.Children.Add(Description(
            "只检查随包文件、功能开关与运行时状态；不会探测 Hyper-V、创建会话或启动虚拟机。"));
        diagnosticsHeader.Children.Add(diagnosticsHeading);
        var diagnosticActions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        diagnosticActions.Children.Add(Button(
            "安装/修复服务",
            () => _ = RunSessionBrokerServiceCommandAsync("--install")));
        diagnosticActions.Children.Add(Button(
            "卸载服务",
            () => _ = RunSessionBrokerServiceCommandAsync("--uninstall")));
        diagnosticActions.Children.Add(Button(
            "刷新诊断",
            OnRefreshDesktopDiagnostics));
        diagnosticActions.Children.Add(Button(
            "清理遗留本机会话",
            () => _ = RecoverLocalChildSessionAsync()));
        Grid.SetRow(diagnosticActions, 1);
        Grid.SetColumnSpan(diagnosticActions, 2);
        diagnosticsHeader.Children.Add(diagnosticActions);
        diagnosticsPanel.Children.Add(diagnosticsHeader);
        _desktopDiagnosticsRows = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        diagnosticsPanel.Children.Add(_desktopDiagnosticsRows);
        diagnosticsCard.Child = diagnosticsPanel;
        root.Children.Add(FoldDesktopSection("诊断", diagnosticsCard));

        var activityCard = Card();
        activityCard.Margin = new Thickness(0, 14, 0, 0);
        var activityPanel = new StackPanel();
        activityPanel.Children.Add(SectionTitle("运行状态与最近活动"));
        var activityStatus = Description("");
        activityStatus.Margin = new Thickness(0, 4, 0, 8);
        activityStatus.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text)) { Source = _desktopSessionStatus });
        activityStatus.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = _desktopSessionStatus });
        activityPanel.Children.Add(activityStatus);
        _desktopActivityList = new ListBox
        {
            Height = 120,
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        activityPanel.Children.Add(_desktopActivityList);
        activityCard.Child = activityPanel;
        root.Children.Add(activityCard);

        RefreshDesktopSessionsPage();
    }

    private Border BuildEnvironmentCard()
    {
        var card = Card();
        card.Margin = new Thickness(0);
        var outer = new StackPanel();
        outer.Children.Add(SectionTitle("本地分身与虚拟环境"));
        outer.Children.Add(Description("左侧选择和编辑环境，右侧管理所选类型的设置。"));
        _desktopEnvironmentColumns = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        _desktopEnvironmentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _desktopEnvironmentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        _desktopEnvironmentColumns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var panel = new StackPanel();
        var details = new StackPanel();
        Grid.SetColumn(details, 2);
        _desktopEnvironmentColumns.Children.Add(panel);
        _desktopEnvironmentColumns.Children.Add(details);
        outer.Children.Add(_desktopEnvironmentColumns);
        _desktopEnvironmentFilter = new ComboBox { ItemsSource = new[] { "全部环境", "本地分身", "虚拟机环境" },
            SelectedIndex = 0, Style = (Style)FindResource("SettingsComboBoxStyle"), Margin = new Thickness(0, 8, 0, 0) };
        _desktopEnvironmentFilter.SelectionChanged += (_, _) => RefreshDesktopSessionsPage();
        panel.Children.Add(_desktopEnvironmentFilter);
        _desktopEnvironmentList = new ListBox
        {
            Height = 132,
            Margin = new Thickness(0, 10, 0, 8),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopEnvironmentList.ItemTemplate = BuildEnvironmentItemTemplate();
        _desktopEnvironmentList.SelectionChanged +=
            OnDesktopEnvironmentSelectionChanged;
        panel.Children.Add(_desktopEnvironmentList);
        _desktopAdvancedButton = Button("高级设置", ToggleDesktopAdvancedPanel);
        _desktopAdvancedButton.Visibility = Visibility.Collapsed;
        panel.Children.Add(ButtonRow(
            Button("添加", OnAddDesktopEnvironment),
            Button("删除", OnRemoveDesktopEnvironment),
            Button("打开视图", OnStartDesktopEnvironment),
            Button("停止", OnStopDesktopEnvironment)));
        _desktopVmActionRow = ButtonRow(
            Button("开机", () => _ = RunEnvironmentPowerAsync("start")),
            Button("关机", () => _ = RunEnvironmentPowerAsync("shutdown")),
            Button("保存", () => _ = RunEnvironmentPowerAsync("save")),
            _desktopAdvancedButton);
        _desktopVmActionRow.Visibility = Visibility.Collapsed;
        panel.Children.Add(_desktopVmActionRow);

        var form = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0)
        };
        form.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(110)
        });
        form.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 6; index++)
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _desktopEnvironmentName = AddField(
            form, 0, "名称", new TextBox());
        _desktopBackendKind = AddComboField(
            form,
            1,
            "后端",
            Enum.GetValues<DesktopSessionBackendKind>());
        _desktopBackendProfile = AddField(
            form, 2, "配置 ID", new TextBox());
        _desktopStartupPolicy = AddComboField(
            form,
            3,
            "启动策略",
            Enum.GetValues<SessionStartupPolicy>());
        _desktopResolution = AddComboField(form, 4, "启动分辨率",
            DesktopResolutionOptions);
        panel.Children.Add(form);
        _desktopBackendKind.SelectionChanged += (_, _) => UpdateDesktopEnvironmentContext();
        _desktopHyperVOptions = new StackPanel();
        details.Children.Add(_desktopHyperVOptions);
        _desktopHyperVOptions.Children.Add(SectionTitle("虚拟机设置"));
        _desktopHyperVOptions.Children.Add(Description("关闭行为"));
        _desktopConsoleClose = new ComboBox { ItemsSource = HyperVCloseOptions, Style = (Style)FindResource("SettingsComboBoxStyle"), Margin = new Thickness(0, 6, 0, 8) };
        _desktopHyperVOptions.Children.Add(_desktopConsoleClose);
        _desktopEnvironmentGpu = new CheckBox { Content = "共享主机显卡（GPU-P）", ToolTip = "实验性；切换立即应用，请先关闭虚拟机。",
            Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 10, 0, 6) };
        _desktopEnvironmentGpu.Click += async (_, _) => await ToggleEnvironmentGpuAsync();
        _desktopHyperVOptions.Children.Add(_desktopEnvironmentGpu);
        _desktopUseSavedLogin = new CheckBox { Content = "使用保存凭据进入桌面", ToolTip = "需要来宾网络，首次连接确认系统证书。",
            Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 8, 0, 6) };
        _desktopHyperVOptions.Children.Add(_desktopUseSavedLogin);
        _desktopHyperVOptions.Children.Add(Description("硬件、共享位置和网络登录选项请点击左侧“高级设置”。"));
        _desktopSharingPanel = new StackPanel();
        _desktopHostSharing = new CheckBox { Content = "Hyper-V 共享宿主磁盘／文件夹（读写）", Style = (Style)FindResource("ToggleSwitchStyle"), Margin = new Thickness(0, 10, 0, 6) };
        _desktopSharingPanel.Children.Add(_desktopHostSharing);
        _desktopHostSharedPaths = new TextBox { AcceptsReturn = true, Height = 85, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = (Style)FindResource("SettingsTextBoxStyle") };
        _desktopSharingPanel.Children.Add(_desktopHostSharedPaths);
        _desktopSharingPanel.Children.Add(Button("添加磁盘或文件夹", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择共享磁盘根目录或文件夹" };
            if (dialog.ShowDialog(this) == true)
                _desktopHostSharedPaths.Text = string.Join(Environment.NewLine, _desktopHostSharedPaths.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Append(dialog.FolderName).Distinct(StringComparer.OrdinalIgnoreCase));
        }));
        _desktopSharingPanel.Children.Add(Description("每行一个本机完整路径。保存环境并应用后，完全关闭并重新打开连接。磁盘根目录保留原盘符；文件夹会映射为临时盘符，在来宾“此电脑”查看。删除一行即可取消共享。来宾可修改文件；应用和游戏可能仍需在来宾安装依赖。"));
        panel.Children.Add(Description("分身内切换分辨率后会自动记住，下次启动沿用。"));
        panel.Children.Add(ButtonRow(
            Button("保存环境", OnSaveDesktopEnvironment)));
        _desktopSessionStatus = Description("选择环境后打开视图，或管理对应的分身设置。");
        _desktopSessionStatus.Margin = new Thickness(0, 8, 0, 0);
        panel.Children.Add(_desktopSessionStatus);

        var startupTitle = SectionTitle("自启动应用");
        startupTitle.Margin = new Thickness(0, 16, 0, 4);
        var startupPanel = new StackPanel();
        _desktopLocalOptions = startupPanel;
        details.Children.Add(startupPanel);
        startupPanel.Children.Add(startupTitle);
        _desktopStartupList = new ListBox
        {
            Height = 96,
            DisplayMemberPath = nameof(SessionStartupEntry.DisplayName),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopStartupList.SelectionChanged +=
            OnStartupEntrySelectionChanged;
        startupPanel.Children.Add(_desktopStartupList);
        startupPanel.Children.Add(ButtonRow(
            Button("选择应用", OnAddStartupEntry),
            Button("删除", OnRemoveStartupEntry)));
        var startupForm = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        startupForm.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(110)
        });
        startupForm.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 5; index++)
            startupForm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _startupPath = AddField(
            startupForm, 0, "路径", new TextBox());
        _startupArguments = AddField(
            startupForm, 1, "参数（每行一个）", new TextBox());
        _startupWorkingDirectory = AddField(
            startupForm, 2, "工作目录", new TextBox());
        _startupDelay = AddField(
            startupForm, 3, "延迟秒数", new TextBox());
        var startupChecks = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        _startupWaitForExit = new CheckBox
        {
            Content = "等待退出",
            Style = (Style)FindResource("SettingsCheckBoxStyle")
        };
        _startupEnabled = new CheckBox
        {
            Content = "启用",
            Margin = new Thickness(14, 0, 0, 0),
            Style = (Style)FindResource("SettingsCheckBoxStyle")
        };
        startupChecks.Children.Add(_startupWaitForExit);
        startupChecks.Children.Add(_startupEnabled);
        Grid.SetRow(startupChecks, 4);
        Grid.SetColumn(startupChecks, 1);
        startupForm.Children.Add(startupChecks);
        startupPanel.Children.Add(startupForm);
        startupPanel.Children.Add(ButtonRow(
            Button("保存自启动项", OnSaveStartupEntry)));

        card.Child = outer;
        InitializeVmStatusPolling(panel);
        return card;
    }

    private Border BuildTaskCard()
    {
        var card = Card();
        card.Margin = new Thickness(0);
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("自动任务"));
        _desktopTaskBackendTabs = new TabControl { Style = (Style)FindResource("SettingsTabControlStyle") };
        _desktopTaskBackendTabs.Items.Add(new TabItem { Header="本地分身",Style=(Style)FindResource("NavigationTabItemStyle") });
        _desktopTaskBackendTabs.Items.Add(new TabItem { Header="虚拟机",Style=(Style)FindResource("NavigationTabItemStyle") });
        _desktopTaskBackendTabs.Items.Add(new TabItem { Header="全局任务",Style=(Style)FindResource("NavigationTabItemStyle") });
        _desktopTaskBackendTabs.SelectedIndex=0;
        panel.Children.Add(_desktopTaskBackendTabs);
        _desktopTaskScope = new ComboBox
        {
            DisplayMemberPath = nameof(EnvironmentProfile.Name),
            Style = (Style)FindResource("SettingsComboBoxStyle"),
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = "选择分身，仅显示此分身的自动任务"
        };
        _desktopTaskScope.SelectionChanged += OnDesktopTaskScopeChanged;
        panel.Children.Add(_desktopTaskScope);
        panel.Children.Add(Description(
            "任务按步骤顺序执行；点击、输入和脚本不会自动重试。"));
        _desktopTaskList = new ListBox
        {
            Height = 116,
            Margin = new Thickness(0, 10, 0, 8),
            ItemTemplate = BuildTaskItemTemplate(),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopTaskList.SelectionChanged += OnDesktopTaskSelectionChanged;
        panel.Children.Add(_desktopTaskList);
        panel.Children.Add(ButtonRow(
            Button("删除任务", OnRemoveDesktopTask),
            Button("运行任务", OnRunDesktopTask),
            Button("停止最近运行", OnCancelDesktopTask)));

        var manual = new StackPanel();
        manual.Children.Add(ButtonRow(Button("添加任务", OnAddDesktopTask)));
        var taskForm = new Grid
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        taskForm.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(110)
        });
        taskForm.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 6; index++)
            taskForm.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _desktopTaskName = AddField(
            taskForm, 0, "名称", new TextBox());
        _desktopTaskEnvironment = AddComboField(
            taskForm,
            1,
            "环境",
            Array.Empty<EnvironmentProfile>());
        _desktopTaskTrigger = AddComboField(
            taskForm,
            2,
            "触发",
            Enum.GetValues<TaskTriggerKind>());
        _desktopTaskTime = AddField(
            taskForm, 3, "本地时间 HH:mm", new TextBox());
        _desktopTaskTimeZone = AddField(
            taskForm, 4, "时区 ID", new TextBox());
        _desktopTaskEnabled = new CheckBox
        {
            Content = "启用任务",
            Style = (Style)FindResource("SettingsCheckBoxStyle")
        };
        Grid.SetRow(_desktopTaskEnabled, 5);
        Grid.SetColumn(_desktopTaskEnabled, 1);
        taskForm.Children.Add(_desktopTaskEnabled);
        manual.Children.Add(taskForm);
        manual.Children.Add(ButtonRow(
            Button("保存任务", OnSaveDesktopTask)));

        manual.Children.Add(SectionTitle("步骤"));
        _desktopStepList = new ListBox
        {
            Height = 108,
            DisplayMemberPath = nameof(SessionTaskStep.Kind),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopStepList.SelectionChanged += OnDesktopStepSelectionChanged;
        manual.Children.Add(_desktopStepList);
        manual.Children.Add(ButtonRow(
            Button("添加步骤", OnAddDesktopStep),
            Button("上移", () => MoveDesktopStep(-1)),
            Button("下移", () => MoveDesktopStep(1)),
            Button("删除步骤", OnRemoveDesktopStep)));

        var stepKindField = new Grid();
        stepKindField.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(110)
        });
        stepKindField.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _desktopStepKind = AddComboField(
            stepKindField,
            0,
            "步骤类型",
            Enum.GetValues<SessionCommandKind>());
        _desktopStepKind.SelectionChanged +=
            (_, _) => RebuildStepParameterInputs();
        manual.Children.Add(stepKindField);
        _desktopStepParameters = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        manual.Children.Add(_desktopStepParameters);
        var limits = new Grid();
        limits.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        limits.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(12)
        });
        limits.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        _desktopStepTimeout = FieldWithLabel(
            limits, "超时秒数（1–600）", 0);
        _desktopStepRetry = FieldWithLabel(
            limits, "重试次数（0–3）", 2);
        manual.Children.Add(limits);
        manual.Children.Add(ButtonRow(
            Button("保存步骤", OnSaveDesktopStep)));
        _desktopTaskManualPanel = new Expander
        {
            Header = "手动添加", Content = manual, IsExpanded = false,
            Style = (Style)FindResource("DesktopSessionExpanderStyle"),
            Margin = new Thickness(0, 10, 0, 0)
        };
        panel.Children.Add(_desktopTaskManualPanel);
        panel.Children.Add(BuildTaskPackageEditor());
        _desktopTaskBackendTabs.SelectionChanged += (_, e) =>
        {
            if(e.Source!=_desktopTaskBackendTabs) return;
            _selectedDesktopTaskId=null; _selectedDesktopStepId=null;
            RefreshDesktopSessionsPage();
        };

        card.Child = panel;
        return card;
    }

    private void RefreshDesktopSessionsPage()
    {
        if (!_desktopSessionsUiReady)
            return;
        var selectedEnvironment = _selectedDesktopEnvironmentId;
        var filter = _desktopEnvironmentFilter.SelectedIndex;
        _desktopEnvironmentList.ItemsSource = _viewModel.DesktopSessionEnvironments
            .Where(item => filter == 0 || (filter == 1 ? item.Backend == DesktopSessionBackendKind.LocalChild : item.Backend == DesktopSessionBackendKind.HyperV)).ToArray();
        RefreshDesktopTaskScope();
        _desktopEnvironmentList.SelectedItem =
            _viewModel.DesktopSessionEnvironments.FirstOrDefault(
                item => item.Id == selectedEnvironment);
        if (_desktopEnvironmentList.SelectedItem is null &&
            _desktopEnvironmentList.Items.Count > 0)
        {
            _desktopEnvironmentList.SelectedIndex = 0;
        }

        RefreshStartupEntries();
        RefreshDesktopSteps();
        RefreshDesktopDiagnostics();
        RefreshDesktopActivity();
    }

    private void OnRefreshDesktopDiagnostics(
        object sender,
        RoutedEventArgs e)
    {
        RefreshDesktopDiagnostics();
        _ = RefreshHyperVFeatureStatusAsync();
    }

    private async Task RunSessionBrokerServiceCommandAsync(string argument)
    {
        var executable = Path.Combine(
            AppContext.BaseDirectory,
            "desktop-sessions",
            "broker",
            "YueYuOrb.SessionBroker.exe");
        if (!File.Exists(executable))
        {
            SetDesktopSessionStatus(
                "当前包未包含桌面会话 Broker，请使用完整发布包。",
                false);
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                SetDesktopSessionStatus(
                    "无法启动桌面会话服务管理器。",
                    false);
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            var success = process.ExitCode == 0;
            SetDesktopSessionStatus(
                success
                    ? argument == "--install"
                        ? "桌面会话服务已安装并启动。"
                        : "桌面会话服务已卸载。"
                    : $"桌面会话服务操作失败，退出码 {process.ExitCode}。",
                success);
            RefreshDesktopSessionsPage();
        }
        catch (System.ComponentModel.Win32Exception exception)
            when (exception.NativeErrorCode == 1223)
        {
            SetDesktopSessionStatus("已取消管理员授权。", false);
        }
        catch (Exception exception)
        {
            SetDesktopSessionStatus(
                $"桌面会话服务操作失败：{exception.Message}",
                false);
        }
    }

    private void RefreshDesktopDiagnostics()
    {
        if (_desktopDiagnosticsRows is null)
            return;
        _desktopDiagnosticsRows.Children.Clear();
        var diagnostics = _desktopDiagnostics.Inspect(
            _viewModel.BuildDesktopSessionDraft(),
            _desktopSessionActions?.IsRuntimeAvailable == true,
            _hyperVFeatureSnapshot);
        foreach (var item in diagnostics)
        {
            var row = new StackPanel
            {
                Margin = new Thickness(0, 3, 0, 3)
            };
            row.Children.Add(new TextBlock
            {
                Text = $"{item.Name} · {item.State}",
                FontWeight = FontWeights.SemiBold,
                Foreground = item.State is
                    "未准备" or "不完整" or
                    "未提供" or "不可用" or "未开放"
                    ? System.Windows.Media.Brushes.IndianRed
                    : (System.Windows.Media.Brush)
                        FindResource("SettingsInkBrush")
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Detail,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72,
                Foreground = (System.Windows.Media.Brush)
                    FindResource("SettingsMutedBrush")
            });
            _desktopDiagnosticsRows.Children.Add(row);
        }
    }

    private void OnDesktopEnvironmentSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_desktopEnvironmentList.SelectedItem is not EnvironmentProfile
            environment)
        {
            _desktopHyperVOptions.Visibility = Visibility.Collapsed;
            _desktopLocalOptions.Visibility = Visibility.Collapsed;
            _desktopAdvancedButton.Visibility = Visibility.Collapsed;
            _desktopVmActionRow.Visibility = Visibility.Collapsed;
            if (_desktopAdvancedPopup is not null) _desktopAdvancedPopup.IsOpen = false;
            if (_desktopAdvancedPanel is not null) _desktopAdvancedPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _selectedDesktopEnvironmentId = environment.Id;
        _desktopEnvironmentName.Text = environment.Name;
        _desktopBackendKind.SelectedItem = environment.Backend;
        _desktopBackendProfile.Text = environment.BackendProfileId;
        _desktopStartupPolicy.SelectedItem = environment.StartupPolicy;
        var resolution = $"{environment.DesktopWidth} × {environment.DesktopHeight}";
        if (!_desktopResolution.Items.Contains(resolution))
            _desktopResolution.ItemsSource = _desktopResolution.Items.Cast<string>().Append(resolution).ToArray();
        _desktopResolution.SelectedItem = resolution;
        _desktopConsoleClose.SelectedIndex = (int)environment.ConsoleCloseAction;
        _desktopHostSharing.IsChecked = environment.HostSharingEnabled;
        _desktopEnvironmentGpu.IsChecked = environment.GpuSharingEnabled;
        _desktopUseSavedLogin.IsChecked = environment.UseAuthenticatedRdp;
        _desktopEnvironmentGpu.Visibility = environment.Backend == DesktopSessionBackendKind.HyperV ? Visibility.Visible : Visibility.Collapsed;
        _desktopHyperVOptions.Visibility = environment.Backend == DesktopSessionBackendKind.HyperV ? Visibility.Visible : Visibility.Collapsed;
        if (environment.Backend == DesktopSessionBackendKind.HyperV && Guid.TryParse(environment.BackendProfileId, out var gpuVmId))
            _ = RefreshEnvironmentGpuStateAsync(environment.Id, gpuVmId);
        _desktopHostSharedPaths.Text = string.Join(Environment.NewLine, environment.HostSharedPaths);
        UpdateDesktopEnvironmentContext();
        SynchronizeSelectedVirtualMachine();
        RefreshStartupEntries();
    }

    private void OnAddDesktopEnvironment(
        object sender,
        RoutedEventArgs e)
    {
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            $"桌面分区 { _viewModel.DesktopSessionEnvironments.Count + 1 }",
            DesktopSessionBackendKind.LocalChild,
            "local-child",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);
        _viewModel.AddDesktopSessionEnvironment(environment);
        _selectedDesktopEnvironmentId = environment.Id;
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("已添加环境，点击“应用”保存。");
    }

    private void OnSaveDesktopEnvironment(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedDesktopEnvironmentId is not { } environmentId)
            return;
        var name = _desktopEnvironmentName.Text.Trim();
        if (name.Length is < 1 or > 80)
        {
            SetDesktopSessionStatus("环境名称必须为 1–80 字。", false);
            return;
        }

        var backend = _desktopBackendKind.SelectedItem is
            DesktopSessionBackendKind selectedBackend
            ? selectedBackend
            : DesktopSessionBackendKind.LocalChild;
        var policy = _desktopStartupPolicy.SelectedItem is
            SessionStartupPolicy selectedPolicy
            ? selectedPolicy
            : SessionStartupPolicy.EnvironmentDefault;
        var resolutionParts = (_desktopResolution.SelectedItem as string ?? "1280 × 720").Split('×');
        var width = int.Parse(resolutionParts[0].Trim(), CultureInfo.InvariantCulture);
        var height = int.Parse(resolutionParts[1].Trim(), CultureInfo.InvariantCulture);
        var sharedPaths = _desktopHostSharedPaths.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (backend == DesktopSessionBackendKind.HyperV && _desktopHostSharing.IsChecked == true && (sharedPaths.Length > 16 || sharedPaths.Any(path => path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))))
        { SetDesktopSessionStatus("共享最多支持 16 个有效的本机磁盘／文件夹路径。", false); return; }
        _viewModel.UpdateDesktopSessionEnvironment(
            environmentId,
            environment => environment with
            {
                Name = name,
                Backend = backend,
                BackendProfileId =
                    _desktopBackendProfile.Text.Trim(),
                StartupPolicy = policy,
                DesktopWidth = width,
                DesktopHeight = height,
                ConsoleCloseAction = (HyperVConsoleCloseAction)Math.Clamp(_desktopConsoleClose.SelectedIndex, 0, 2),
                HostSharingEnabled = _desktopHostSharing.IsChecked == true,
                UseAuthenticatedRdp = _desktopUseSavedLogin.IsChecked == true,
                HostSharedPaths = sharedPaths
            });
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("环境已更新，点击“应用”保存。");
    }

    private void OnRemoveDesktopEnvironment(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedDesktopEnvironmentId is not { } id)
            return;
        _viewModel.RemoveDesktopSessionEnvironment(id);
        _selectedDesktopEnvironmentId = null;
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("环境已从草稿移除，点击“应用”保存。");
    }

    private void OnStartDesktopEnvironment(
        object sender,
        RoutedEventArgs e) =>
        _ = InvokeDesktopSessionActionAsync(
            () => _desktopSessionActions?.StartEnvironmentAsync(
                _selectedDesktopEnvironmentId ??
                throw new InvalidOperationException("请选择环境。"),
                CancellationToken.None) ??
                Task.FromResult(Unavailable()),
            "环境启动请求已提交。");

    private void OnStopDesktopEnvironment(
        object sender,
        RoutedEventArgs e)
    {
        var selected = _viewModel.DesktopSessionEnvironments.FirstOrDefault(
            environment => environment.Id == _selectedDesktopEnvironmentId);
        if (selected?.Backend == DesktopSessionBackendKind.LocalChild &&
            MessageBox.Show(this,
                "停止将注销此本机分身并关闭其中应用，请先保存工作。主桌面不受影响。继续？",
                "停止本机分身", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _ = InvokeDesktopSessionActionAsync(
            () => _desktopSessionActions?.StopEnvironmentAsync(
                _selectedDesktopEnvironmentId ??
                throw new InvalidOperationException("请选择环境。"),
                CancellationToken.None) ??
                Task.FromResult(Unavailable()),
            "环境停止请求已提交。");
    }

    private void OnRunDesktopTask(
        object sender,
        RoutedEventArgs e) =>
        _ = InvokeDesktopSessionActionAsync(
            () => _desktopSessionActions?.RunTaskAsync(
                _selectedDesktopTaskId ??
                throw new InvalidOperationException("请选择任务。"),
                CancellationToken.None) ??
                Task.FromResult(Unavailable()),
            "任务运行请求已提交。");

    private void OnCancelDesktopTask(
        object sender,
        RoutedEventArgs e) =>
        _ = InvokeDesktopSessionActionAsync(
            () => _desktopSessionActions?.CancelLatestRunAsync(
                CancellationToken.None) ??
                Task.FromResult(Unavailable()),
            "取消请求已提交。");

    private async Task InvokeDesktopSessionActionAsync(
        Func<Task<DesktopSessionActionStatus>> action,
        string submittedMessage)
    {
        SetDesktopSessionStatus(submittedMessage);
        try
        {
            var result = await action();
            SetDesktopSessionStatus(result.Message, result.Success);
            RefreshDesktopActivity();
        }
        catch (Exception exception)
        {
            SetDesktopSessionStatus(
                $"操作失败：{exception.Message}",
                false);
        }
    }

    private static DesktopSessionActionStatus Unavailable() =>
        new(false, "桌面会话运行时尚未配置。");

    private void OnAddStartupEntry(object sender, RoutedEventArgs e)
    {
        if (_selectedDesktopEnvironmentId is not { } environmentId)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要加入自启动的应用",
            Filter = "应用或快捷方式|*.exe;*.lnk"
        };
        if (dialog.ShowDialog(this) != true)
            return;
        var entry = new SessionStartupEntry(
            Guid.NewGuid(),
            Path.GetFileNameWithoutExtension(dialog.FileName),
            dialog.FileName,
            [],
            Path.GetDirectoryName(dialog.FileName),
            _viewModel.DesktopSessionEnvironments
                .First(environment => environment.Id == environmentId)
                .StartupEntries.Count,
            DelaySeconds: 0,
            Enabled: true,
            WaitForExit: false);
        _viewModel.UpdateDesktopSessionEnvironment(
            environmentId,
            environment =>
                DesktopSessionConfigurationEditor.AddStartupEntry(
                    environment,
                    entry));
        _selectedStartupEntryId = entry.Id;
        RefreshDesktopSessionsPage();
    }

    private void OnStartupEntrySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_desktopStartupList.SelectedItem is not SessionStartupEntry entry)
            return;
        _selectedStartupEntryId = entry.Id;
        _startupPath.Text = entry.ExecutablePath;
        _startupArguments.Text = string.Join(
            Environment.NewLine,
            entry.Arguments);
        _startupWorkingDirectory.Text =
            entry.WorkingDirectory ?? "";
        _startupDelay.Text = entry.DelaySeconds.ToString(
            CultureInfo.InvariantCulture);
        _startupWaitForExit.IsChecked = entry.WaitForExit;
        _startupEnabled.IsChecked = entry.Enabled;
    }

    private void OnSaveStartupEntry(object sender, RoutedEventArgs e)
    {
        if (_selectedDesktopEnvironmentId is not { } environmentId ||
            _selectedStartupEntryId is not { } entryId)
        {
            return;
        }
        if (!int.TryParse(
                _startupDelay.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var delay) ||
            delay is < 0 or > 300)
        {
            SetDesktopSessionStatus("自启动延迟必须为 0–300 秒。", false);
            return;
        }

        _viewModel.UpdateDesktopSessionEnvironment(
            environmentId,
            environment =>
                DesktopSessionConfigurationEditor.UpdateStartupEntry(
                    environment,
                    entryId,
                    entry => entry with
                    {
                        DisplayName =
                            Path.GetFileNameWithoutExtension(
                                _startupPath.Text),
                        ExecutablePath = _startupPath.Text.Trim(),
                        Arguments = _startupArguments.Text
                            .Split(
                                ['\r', '\n'],
                                StringSplitOptions.RemoveEmptyEntries |
                                StringSplitOptions.TrimEntries),
                        WorkingDirectory = string.IsNullOrWhiteSpace(
                            _startupWorkingDirectory.Text)
                            ? null
                            : _startupWorkingDirectory.Text.Trim(),
                        DelaySeconds = delay,
                        WaitForExit =
                            _startupWaitForExit.IsChecked == true,
                        Enabled = _startupEnabled.IsChecked == true
                    }));
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("自启动项已更新，点击“应用”保存。");
    }

    private void OnRemoveStartupEntry(object sender, RoutedEventArgs e)
    {
        if (_selectedDesktopEnvironmentId is not { } environmentId ||
            _selectedStartupEntryId is not { } entryId)
        {
            return;
        }
        _viewModel.UpdateDesktopSessionEnvironment(
            environmentId,
            environment => environment with
            {
                StartupEntries = environment.StartupEntries
                    .Where(entry => entry.Id != entryId)
                    .ToArray()
            });
        _selectedStartupEntryId = null;
        RefreshDesktopSessionsPage();
    }

    private void OnDesktopTaskSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_desktopTaskList.SelectedItem is not DesktopSessionTask task)
            return;
        _selectedDesktopTaskId = task.Id;
        _desktopTaskName.Text = task.Name;
        _desktopTaskEnvironment.SelectedItem =
            _viewModel.DesktopSessionEnvironments.FirstOrDefault(
                environment =>
                    environment.Id == task.EnvironmentId);
        _desktopTaskTrigger.SelectedItem = task.Trigger.Kind;
        _desktopTaskTime.Text = task.Trigger.LocalTime ?? "";
        _desktopTaskTimeZone.Text =
            task.Trigger.TimeZoneId ?? TimeZoneInfo.Local.Id;
        _desktopTaskEnabled.IsChecked = task.Enabled;
        RefreshTaskPackageEditor(task);
        RefreshDesktopSteps();
    }

    private void OnAddDesktopTask(object sender, RoutedEventArgs e)
    {
        var environment = _desktopTaskScope.SelectedItem as EnvironmentProfile;
        if (environment is null)
        {
            SetDesktopSessionStatus("请先添加环境。", false);
            return;
        }
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            $"任务 {_viewModel.DesktopSessionTasks.Count + 1}",
            environment.Id,
            [],
            new SessionTaskTrigger(
                TaskTriggerKind.Manual,
                null,
                TimeZoneInfo.Local.Id),
            Revision: 1,
            Enabled: true);
        _viewModel.AddDesktopSessionTask(task);
        _selectedDesktopTaskId = task.Id;
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("已添加任务，点击“应用”保存。");
    }

    private void OnSaveDesktopTask(object sender, RoutedEventArgs e)
    {
        if (_selectedDesktopTaskId is not { } taskId ||
            _desktopTaskEnvironment.SelectedItem is not EnvironmentProfile
                environment)
        {
            SetDesktopSessionStatus(
                "请选择任务和环境。",
                false);
            return;
        }
        var name = _desktopTaskName.Text.Trim();
        if (name.Length is < 1 or > 80)
        {
            SetDesktopSessionStatus("任务名称必须为 1–80 字。", false);
            return;
        }
        var triggerKind = _desktopTaskTrigger.SelectedItem is
            TaskTriggerKind selectedTrigger
            ? selectedTrigger
            : TaskTriggerKind.Manual;
        _viewModel.UpdateDesktopSessionTask(
            taskId,
            task => task with
            {
                Name = name,
                EnvironmentId = environment.Id,
                Trigger = new SessionTaskTrigger(
                    triggerKind,
                    triggerKind == TaskTriggerKind.Scheduled
                        ? _desktopTaskTime.Text.Trim()
                        : null,
                    triggerKind == TaskTriggerKind.Scheduled
                        ? _desktopTaskTimeZone.Text.Trim()
                        : _desktopTaskTimeZone.Text.Trim()),
                Enabled = _desktopTaskEnabled.IsChecked == true
            });
        RefreshDesktopSessionsPage();
        SetDesktopSessionStatus("任务已更新，点击“应用”保存。");
    }

    private void OnRemoveDesktopTask(object sender, RoutedEventArgs e)
    {
        if (_selectedDesktopTaskId is not { } id)
            return;
        _viewModel.RemoveDesktopSessionTask(id);
        _selectedDesktopTaskId = null;
        RefreshDesktopSessionsPage();
    }

    private void OnDesktopStepSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_desktopStepList.SelectedItem is not SessionTaskStep step)
            return;
        _selectedDesktopStepId = step.Id;
        _desktopStepKind.SelectedItem = step.Kind;
        _desktopStepTimeout.Text = step.TimeoutSeconds.ToString(
            CultureInfo.InvariantCulture);
        _desktopStepRetry.Text = step.RetryCount.ToString(
            CultureInfo.InvariantCulture);
        RebuildStepParameterInputs(step.Parameters);
    }

    private void OnAddDesktopStep(object sender, RoutedEventArgs e)
    {
        if (SelectedTask() is not { } task)
            return;
        var kind = SessionCommandKind.Delay;
        var step = new SessionTaskStep(
            Guid.NewGuid(),
            kind,
            DefaultStepParameters(kind),
            TimeoutSeconds: 30,
            RetryCount: 0);
        _viewModel.UpdateDesktopSessionTask(
            task.Id,
            value => value with
            {
                Steps = [.. value.Steps, step]
            });
        _selectedDesktopStepId = step.Id;
        RefreshDesktopSteps();
        _desktopStepList.SelectedItem = _viewModel.DesktopSessionTasks
            .First(item => item.Id == task.Id)
            .Steps.First(item => item.Id == step.Id);
    }

    private void OnSaveDesktopStep(object sender, RoutedEventArgs e)
    {
        if (SelectedTask() is not { } task ||
            _selectedDesktopStepId is not { } stepId ||
            _desktopStepKind.SelectedItem is not SessionCommandKind kind ||
            !int.TryParse(
                _desktopStepTimeout.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var timeout) ||
            timeout is < 1 or > 600 ||
            !int.TryParse(
                _desktopStepRetry.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var retry) ||
            retry is < 0 or > 3)
        {
            SetDesktopSessionStatus(
                "步骤类型、超时或重试次数无效。",
                false);
            return;
        }

        var parameters = _stepParameterInputs.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Text,
            StringComparer.Ordinal);
        _viewModel.UpdateDesktopSessionTask(
            task.Id,
            value => value with
            {
                Steps = value.Steps
                    .Select(step => step.Id == stepId
                        ? step with
                        {
                            Kind = kind,
                            Parameters = parameters,
                            TimeoutSeconds = timeout,
                            RetryCount = retry
                        }
                        : step)
                    .ToArray()
            });
        RefreshDesktopSteps();
        SetDesktopSessionStatus("任务步骤已更新，点击“应用”保存。");
    }

    private void OnRemoveDesktopStep(object sender, RoutedEventArgs e)
    {
        if (SelectedTask() is not { } task ||
            _selectedDesktopStepId is not { } stepId)
        {
            return;
        }
        _viewModel.UpdateDesktopSessionTask(
            task.Id,
            value => value with
            {
                Steps = value.Steps
                    .Where(step => step.Id != stepId)
                    .ToArray()
            });
        _selectedDesktopStepId = null;
        RefreshDesktopSteps();
    }

    private void MoveDesktopStep(int delta)
    {
        if (SelectedTask() is not { } task ||
            _selectedDesktopStepId is not { } stepId)
        {
            return;
        }
        var steps = task.Steps.ToList();
        var index = steps.FindIndex(step => step.Id == stepId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= steps.Count)
            return;
        (steps[index], steps[target]) = (steps[target], steps[index]);
        _viewModel.UpdateDesktopSessionTask(
            task.Id,
            value => value with { Steps = steps });
        RefreshDesktopSteps();
        _desktopStepList.SelectedItem = steps[target];
    }

    private void RefreshStartupEntries()
    {
        var environment = _viewModel.DesktopSessionEnvironments
            .FirstOrDefault(item =>
                item.Id == _selectedDesktopEnvironmentId);
        _desktopStartupList.ItemsSource =
            environment?.StartupEntries ?? [];
        _desktopStartupList.SelectedItem = environment?.StartupEntries
            .FirstOrDefault(entry =>
                entry.Id == _selectedStartupEntryId);
    }

    private void RefreshDesktopSteps()
    {
        var task = SelectedTask();
        _desktopStepList.ItemsSource = task?.Steps ?? [];
        _desktopStepList.SelectedItem = task?.Steps.FirstOrDefault(
            step => step.Id == _selectedDesktopStepId);
    }

    private DesktopSessionTask? SelectedTask() =>
        _viewModel.DesktopSessionTasks.FirstOrDefault(
            task => task.Id == _selectedDesktopTaskId);

    private void RefreshDesktopActivity()
    {
        if (_desktopActivityList is null)
            return;
        _desktopActivityList.ItemsSource =
            _desktopSessionActions?.GetRecentActivity() ??
            ["尚无桌面会话活动。"];
        if(_automationActivityList is not null)_automationActivityList.ItemsSource=_desktopActivityList.ItemsSource;
    }

    private void RebuildStepParameterInputs(
        IReadOnlyDictionary<string, string>? values = null)
    {
        if (_desktopStepParameters is null)
            return;
        _stepParameterInputs.Clear();
        _desktopStepParameters.Children.Clear();
        var kind = _desktopStepKind.SelectedItem is SessionCommandKind selected
            ? selected
            : SessionCommandKind.Delay;
        foreach (var (key, label) in StepParameterFields(kind))
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 3, 0, 3)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(150)
            });
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var caption = new TextBlock
            {
                Text = label,
                Style = (Style)FindResource("SettingsLabelStyle"),
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(caption);
            var input = new TextBox
            {
                Text = values?.GetValueOrDefault(key) ??
                    DefaultStepParameters(kind).GetValueOrDefault(key) ??
                    "",
                Style = (Style)FindResource("SettingsTextBoxStyle")
            };
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            _stepParameterInputs[key] = input;
            _desktopStepParameters.Children.Add(grid);
        }
    }

    private static IReadOnlyList<(string Key, string Label)>
        StepParameterFields(SessionCommandKind kind) =>
        kind switch
        {
            SessionCommandKind.SetBrightness => [("percent", "主机全部屏幕亮度（0–100，仅全局任务）")],
            SessionCommandKind.CloseApplication => [("path", "目标应用完整路径"), ("force", "强制关闭 true/false")],
            SessionCommandKind.ObserveDesktop => [("mode", "windows / uiTree / screenshot")],
            SessionCommandKind.RunSystemScript => [("scriptName", "内嵌脚本名称（仅虚拟机管理员）"), ("arguments", "参数 JSON 数组")],
            SessionCommandKind.LaunchApplication =>
            [
                ("startupEntry", "启动项 JSON")
            ],
            SessionCommandKind.WaitForWindow =>
            [
                ("title", "窗口标题"),
                ("processName", "进程名")
            ],
            SessionCommandKind.ClickElement =>
            [
                ("automationId", "AutomationId"),
                ("name", "控件名称"),
                ("className", "类名"),
                ("controlType", "控件类型"),
                ("strategy", "坐标模式 coordinates"),
                ("x", "X"),
                ("y", "Y"),
                ("expectedWidth", "预期宽度"),
                ("expectedHeight", "预期高度")
            ],
            SessionCommandKind.TypeText =>
            [
                ("automationId", "AutomationId"),
                ("name", "控件名称"),
                ("text", "文本（会随任务保存）")
            ],
            SessionCommandKind.SendKeys =>
            [
                ("keys", "按键序列，如 Ctrl+S")
            ],
            SessionCommandKind.Delay =>
            [
                ("milliseconds", "毫秒（0–600000）")
            ],
            SessionCommandKind.CaptureScreenshot =>
            [
                ("left", "Left"),
                ("top", "Top"),
                ("width", "宽度"),
                ("height", "高度"),
                ("path", "来宾绝对保存路径")
            ],
            SessionCommandKind.RunScript =>
            [
                ("scriptName", "内嵌脚本名称（可留空使用路径）"),
                ("scriptPath", "脚本绝对路径"),
                ("arguments", "参数 JSON 数组")
            ],
            _ => []
        };

    private static Dictionary<string, string> DefaultStepParameters(
        SessionCommandKind kind) =>
        kind switch
        {
            SessionCommandKind.SetBrightness => new(StringComparer.Ordinal) { ["percent"]="50" },
            SessionCommandKind.LaunchApplication =>
                new(StringComparer.Ordinal)
                {
                    ["startupEntry"] = ""
                },
            SessionCommandKind.WaitForWindow =>
                new(StringComparer.Ordinal)
                {
                    ["title"] = "",
                    ["processName"] = ""
                },
            SessionCommandKind.ClickElement =>
                new(StringComparer.Ordinal)
                {
                    ["automationId"] = "",
                    ["name"] = "",
                    ["className"] = "",
                    ["controlType"] = ""
                },
            SessionCommandKind.TypeText =>
                new(StringComparer.Ordinal)
                {
                    ["automationId"] = "",
                    ["name"] = "",
                    ["text"] = ""
                },
            SessionCommandKind.SendKeys =>
                new(StringComparer.Ordinal)
                {
                    ["keys"] = ""
                },
            SessionCommandKind.Delay =>
                new(StringComparer.Ordinal)
                {
                    ["milliseconds"] = "1000"
                },
            SessionCommandKind.CaptureScreenshot =>
                new(StringComparer.Ordinal)
                {
                    ["left"] = "0",
                    ["top"] = "0",
                    ["width"] = "800",
                    ["height"] = "600",
                    ["path"] = ""
                },
            SessionCommandKind.RunSystemScript => new(StringComparer.Ordinal) { ["scriptName"] = "", ["arguments"] = "[]" },
            SessionCommandKind.RunScript =>
                new(StringComparer.Ordinal)
                {
                    ["scriptPath"] = "",
                    ["arguments"] = "[]"
                },
            _ => new Dictionary<string, string>(
                StringComparer.Ordinal)
        };

    private void SetDesktopSessionStatus(
        string message,
        bool? success = true)
    {
        if (_desktopSessionStatus is null)
            return;
        _desktopSessionStatus.Text = message;
        _desktopSessionStatus.Foreground =
            success == false
                ? System.Windows.Media.Brushes.IndianRed
                : (System.Windows.Media.Brush)
                    FindResource("SettingsMutedBrush");
    }

    private Border Card() => new()
    {
        Style = (Style)FindResource("SettingsCardStyle"),
        Padding = new Thickness(20, 16, 20, 18),
        Margin = new Thickness(0, 0, 0, 12)
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold
    };

    private static TextBlock Description(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.72,
        Margin = new Thickness(0, 3, 0, 0),
        FontSize = 12
    };

    private Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("ActionButtonStyle"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private Button Button(
        string text,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("ActionButtonStyle"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += handler;
        return button;
    }

    private static WrapPanel ButtonRow(params Button[] buttons)
    {
        var panel = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0)
        };
        foreach (var button in buttons)
            panel.Children.Add(button);
        return panel;
    }

    private TextBox AddField(
        Grid grid,
        int row,
        string label,
        TextBox input)
    {
        var caption = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SettingsLabelStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4)
        };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        input.Style = (Style)FindResource("SettingsTextBoxStyle");
        input.Margin = new Thickness(0, 4, 0, 4);
        Grid.SetRow(input, row);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);
        return input;
    }

    private ComboBox AddComboField<T>(
        Grid grid,
        int row,
        string label,
        IEnumerable<T> values)
        where T : notnull
    {
        var caption = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SettingsLabelStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4)
        };
        Grid.SetRow(caption, row);
        grid.Children.Add(caption);
        var combo = new ComboBox
        {
            ItemsSource = values,
            Style = (Style)FindResource("SettingsComboBoxStyle"),
            Margin = new Thickness(0, 4, 0, 4)
        };
        Grid.SetRow(combo, row);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return combo;
    }

    private TextBox FieldWithLabel(
        Grid grid,
        string label,
        int column)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SettingsLabelStyle"),
            Margin = new Thickness(0, 0, 0, 4)
        });
        var input = new TextBox
        {
            Style = (Style)FindResource("SettingsTextBoxStyle")
        };
        panel.Children.Add(input);
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
        return input;
    }
}
