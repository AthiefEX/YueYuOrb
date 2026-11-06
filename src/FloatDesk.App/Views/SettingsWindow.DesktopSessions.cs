using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
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
            Content = "启用桌面分身",
            Style = (Style)FindResource("ToggleSwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 0, 0)
        };
        enabledToggle.SetBinding(
            System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(nameof(ViewModels.SettingsViewModel.DesktopSessionsEnabled))
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        Grid.SetColumn(enabledToggle, 1);
        headerGrid.Children.Add(enabledToggle);
        header.Child = headerGrid;
        root.Children.Add(header);

        var columns = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };
        columns.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(14)
        });
        columns.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
        columns.Children.Add(BuildEnvironmentCard());
        var taskCard = BuildTaskCard();
        Grid.SetColumn(taskCard, 2);
        columns.Children.Add(taskCard);
        root.Children.Add(columns);

        var diagnosticsCard = Card();
        diagnosticsCard.Margin = new Thickness(0, 14, 0, 0);
        var diagnosticsPanel = new StackPanel();
        var diagnosticsHeader = new Grid();
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
        var refreshDiagnostics = Button(
            "刷新诊断",
            OnRefreshDesktopDiagnostics);
        refreshDiagnostics.Margin = new Thickness(16, 0, 0, 0);
        refreshDiagnostics.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(refreshDiagnostics, 1);
        diagnosticsHeader.Children.Add(refreshDiagnostics);
        diagnosticsPanel.Children.Add(diagnosticsHeader);
        _desktopDiagnosticsRows = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0)
        };
        diagnosticsPanel.Children.Add(_desktopDiagnosticsRows);
        diagnosticsCard.Child = diagnosticsPanel;
        root.Children.Add(diagnosticsCard);

        var activityCard = Card();
        activityCard.Margin = new Thickness(0, 14, 0, 0);
        var activityPanel = new StackPanel();
        activityPanel.Children.Add(SectionTitle("运行状态与最近活动"));
        _desktopSessionStatus = Description("尚未启动桌面分区。");
        _desktopSessionStatus.Margin = new Thickness(0, 5, 0, 8);
        activityPanel.Children.Add(_desktopSessionStatus);
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
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("环境"));
        panel.Children.Add(Description(
            "选择可用的 Windows 子会话或已配对的 Hyper-V 虚拟机。"));
        _desktopEnvironmentList = new ListBox
        {
            Height = 132,
            Margin = new Thickness(0, 10, 0, 8),
            DisplayMemberPath = nameof(EnvironmentProfile.Name),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopEnvironmentList.SelectionChanged +=
            OnDesktopEnvironmentSelectionChanged;
        panel.Children.Add(_desktopEnvironmentList);
        panel.Children.Add(ButtonRow(
            Button("添加", OnAddDesktopEnvironment),
            Button("删除", OnRemoveDesktopEnvironment),
            Button("打开视图", OnStartDesktopEnvironment),
            Button("停止", OnStopDesktopEnvironment)));

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
        for (var index = 0; index < 4; index++)
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
        panel.Children.Add(form);
        panel.Children.Add(ButtonRow(
            Button("保存环境", OnSaveDesktopEnvironment)));

        var startupTitle = SectionTitle("自启动应用");
        startupTitle.Margin = new Thickness(0, 16, 0, 4);
        panel.Children.Add(startupTitle);
        _desktopStartupList = new ListBox
        {
            Height = 96,
            DisplayMemberPath = nameof(SessionStartupEntry.DisplayName),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopStartupList.SelectionChanged +=
            OnStartupEntrySelectionChanged;
        panel.Children.Add(_desktopStartupList);
        panel.Children.Add(ButtonRow(
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
        panel.Children.Add(startupForm);
        panel.Children.Add(ButtonRow(
            Button("保存自启动项", OnSaveStartupEntry)));

        card.Child = panel;
        return card;
    }

    private Border BuildTaskCard()
    {
        var card = Card();
        card.Margin = new Thickness(0);
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("自动任务"));
        panel.Children.Add(Description(
            "任务按步骤顺序执行；点击、输入和脚本不会自动重试。"));
        _desktopTaskList = new ListBox
        {
            Height = 116,
            Margin = new Thickness(0, 10, 0, 8),
            DisplayMemberPath = nameof(DesktopSessionTask.Name),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopTaskList.SelectionChanged += OnDesktopTaskSelectionChanged;
        panel.Children.Add(_desktopTaskList);
        panel.Children.Add(ButtonRow(
            Button("添加任务", OnAddDesktopTask),
            Button("删除任务", OnRemoveDesktopTask),
            Button("运行任务", OnRunDesktopTask),
            Button("停止最近运行", OnCancelDesktopTask)));

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
        panel.Children.Add(taskForm);
        panel.Children.Add(ButtonRow(
            Button("保存任务", OnSaveDesktopTask)));

        panel.Children.Add(SectionTitle("步骤"));
        _desktopStepList = new ListBox
        {
            Height = 108,
            DisplayMemberPath = nameof(SessionTaskStep.Kind),
            Style = (Style)FindResource("ToolWindowListBoxStyle")
        };
        _desktopStepList.SelectionChanged += OnDesktopStepSelectionChanged;
        panel.Children.Add(_desktopStepList);
        panel.Children.Add(ButtonRow(
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
        panel.Children.Add(stepKindField);
        _desktopStepParameters = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(_desktopStepParameters);
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
        panel.Children.Add(limits);
        panel.Children.Add(ButtonRow(
            Button("保存步骤", OnSaveDesktopStep)));

        card.Child = panel;
        return card;
    }

    private void RefreshDesktopSessionsPage()
    {
        if (!_desktopSessionsUiReady)
            return;
        var selectedEnvironment = _selectedDesktopEnvironmentId;
        var selectedTask = _selectedDesktopTaskId;
        _desktopEnvironmentList.ItemsSource =
            _viewModel.DesktopSessionEnvironments;
        _desktopTaskList.ItemsSource = _viewModel.DesktopSessionTasks;
        _desktopTaskEnvironment.ItemsSource =
            _viewModel.DesktopSessionEnvironments;
        _desktopEnvironmentList.SelectedItem =
            _viewModel.DesktopSessionEnvironments.FirstOrDefault(
                item => item.Id == selectedEnvironment);
        _desktopTaskList.SelectedItem =
            _viewModel.DesktopSessionTasks.FirstOrDefault(
                item => item.Id == selectedTask);
        if (_desktopEnvironmentList.SelectedItem is null &&
            _viewModel.DesktopSessionEnvironments.Count > 0)
        {
            _desktopEnvironmentList.SelectedIndex = 0;
        }
        if (_desktopTaskList.SelectedItem is null &&
            _viewModel.DesktopSessionTasks.Count > 0)
        {
            _desktopTaskList.SelectedIndex = 0;
        }

        RefreshStartupEntries();
        RefreshDesktopSteps();
        RefreshDesktopDiagnostics();
        RefreshDesktopActivity();
    }

    private void OnRefreshDesktopDiagnostics(
        object sender,
        RoutedEventArgs e) =>
        RefreshDesktopDiagnostics();

    private void RefreshDesktopDiagnostics()
    {
        if (_desktopDiagnosticsRows is null)
            return;
        _desktopDiagnosticsRows.Children.Clear();
        var diagnostics = _desktopDiagnostics.Inspect(
            _viewModel.BuildDesktopSessionDraft(),
            _desktopSessionActions?.IsRuntimeAvailable == true);
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
            return;
        }

        _selectedDesktopEnvironmentId = environment.Id;
        _desktopEnvironmentName.Text = environment.Name;
        _desktopBackendKind.SelectedItem = environment.Backend;
        _desktopBackendProfile.Text = environment.BackendProfileId;
        _desktopStartupPolicy.SelectedItem = environment.StartupPolicy;
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
        _viewModel.UpdateDesktopSessionEnvironment(
            environmentId,
            environment => environment with
            {
                Name = name,
                Backend = backend,
                BackendProfileId =
                    _desktopBackendProfile.Text.Trim(),
                StartupPolicy = policy
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
        RoutedEventArgs e) =>
        _ = InvokeDesktopSessionActionAsync(
            () => _desktopSessionActions?.StopEnvironmentAsync(
                _selectedDesktopEnvironmentId ??
                throw new InvalidOperationException("请选择环境。"),
                CancellationToken.None) ??
                Task.FromResult(Unavailable()),
            "环境停止请求已提交。");

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
        RefreshDesktopSteps();
    }

    private void OnAddDesktopTask(object sender, RoutedEventArgs e)
    {
        var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault();
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
                ("text", "文本或密码")
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
                ("scriptPath", "脚本绝对路径"),
                ("arguments", "参数 JSON 数组")
            ],
            _ => []
        };

    private static Dictionary<string, string> DefaultStepParameters(
        SessionCommandKind kind) =>
        kind switch
        {
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

    private static StackPanel ButtonRow(params Button[] buttons)
    {
        var panel = new StackPanel
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
