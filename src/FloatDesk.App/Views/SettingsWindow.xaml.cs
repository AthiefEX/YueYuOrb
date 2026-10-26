using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatDesk.App.ViewModels;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions SettingsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISettingsCoordinator _settings;
    private readonly AppDataPaths _paths;
    private readonly Func<string?>? _currentAppPath;
    private SettingsViewModel _viewModel;

    public event Action<AppSettings>? Applied;

    public SettingsWindow(
        ISettingsCoordinator settings,
        AppDataPaths paths,
        Func<string?>? currentAppPath = null)
    {
        InitializeComponent();
        _settings = settings;
        _paths = paths;
        _currentAppPath = currentAppPath;
        _viewModel = SettingsViewModel.From(settings.Current);
        DataContext = _viewModel;
        Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
    }

    private void OnSettingsWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowProcedure);
    }

    private nint WindowProcedure(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        const int WmNcHitTest = 0x0084;
        const int HtMaxButton = 9;
        if (message != WmNcHitTest || MaximizeSettingsButton is null)
            return nint.Zero;

        var packed = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        var clientPoint = PointFromScreen(screenPoint);
        var buttonStart = MaximizeSettingsButton.TranslatePoint(
            new Point(0, 0),
            this);
        var buttonEnd = new Point(
            buttonStart.X + MaximizeSettingsButton.ActualWidth,
            buttonStart.Y + MaximizeSettingsButton.ActualHeight);
        if (clientPoint.X >= buttonStart.X &&
            clientPoint.X <= buttonEnd.X &&
            clientPoint.Y >= buttonStart.Y &&
            clientPoint.Y <= buttonEnd.Y)
        {
            handled = true;
            return HtMaxButton;
        }

        return nint.Zero;
    }

    private void OnTitleBarMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may have been released between the event and drag.
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        ToggleMaximize();

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e) =>
        Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnSettingsTabSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (SettingsTabControl is null ||
            !SystemParameters.ClientAreaAnimation ||
            SystemParameters.HighContrast)
        {
            return;
        }

        SettingsTabControl.ApplyTemplate();
        if (SettingsTabControl.Template?.FindName(
                "PART_SelectedContentHost",
                SettingsTabControl) is not ContentPresenter contentHost)
        {
            return;
        }

        contentHost.RenderTransform = new TranslateTransform(0, 6);
        contentHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
        ((TranslateTransform)contentHost.RenderTransform).BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            });
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_viewModel.TryBuildCandidate(
                    _settings.Current,
                    out _,
                    out var validationError))
            {
                SaveStatusText.Text = validationError;
                return;
            }
            var result = await _settings.UpdateAsync(
                current => _viewModel.TryBuildCandidate(
                    current,
                    out var candidate,
                    out _)
                    ? candidate
                    : current,
                CancellationToken.None);
            SaveStatusText.Text = result.Success
                ? "已保存。"
                : result.Message ?? "保存失败。";
            if (!result.Success)
                return;
            var candidate = _settings.Current;
            StartupService.SetEnabled(candidate.General.StartWithWindows);
            Applied?.Invoke(candidate);
            _viewModel = SettingsViewModel.From(candidate);
            DataContext = _viewModel;
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = exception.Message;
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _viewModel = SettingsViewModel.From(_settings.Current);
        DataContext = _viewModel;
        Hide();
    }

    private void OnChooseOrbImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            OrbImageImporter.Import(dialog.FileName, _paths.Assets);
            SaveStatusText.Text = "图片已导入，点击应用使设置生效。";
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = exception.Message;
        }
    }

    private void OnResetOrbImageClick(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_paths.Assets, "orb.png");
        if (File.Exists(path))
            File.Delete(path);
        SaveStatusText.Text = "已恢复默认悬浮球，点击应用使设置生效。";
    }

    private void OnAddExeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "应用程序|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        AddLauncher(dialog.FileName, LauncherKind.Exe);
    }

    private void OnAddShortcutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "快捷方式|*.lnk",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        AddLauncher(dialog.FileName, LauncherKind.Shortcut);
    }

    private void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(this) != true)
            return;
        AddLauncher(dialog.FolderName, LauncherKind.Folder);
    }

    private void AddLauncher(string path, LauncherKind kind)
    {
        var name = kind == LauncherKind.Folder
            ? new DirectoryInfo(path).Name
            : Path.GetFileNameWithoutExtension(path);
        _viewModel.Launchers.Add(new LauncherItem(
            Guid.NewGuid(),
            name,
            kind,
            path,
            [],
            kind == LauncherKind.Exe ? Path.GetDirectoryName(path) : null,
            _viewModel.Launchers.Count));
    }

    private void OnRemoveLauncherClick(object sender, RoutedEventArgs e)
    {
        if (LauncherGrid.SelectedItem is LauncherItem item)
            _viewModel.Launchers.Remove(item);
    }

    private void OnAddLauncherToWheelClick(object sender, RoutedEventArgs e) =>
        SetLauncherWheelVisibility(showInWheel: true);

    private void OnRemoveLauncherFromWheelClick(object sender, RoutedEventArgs e) =>
        SetLauncherWheelVisibility(showInWheel: false);

    private void SetLauncherWheelVisibility(bool showInWheel)
    {
        if (LauncherGrid.SelectedItem is not LauncherItem selected)
        {
            SaveStatusText.Text = "请先在列表中选择一个快捷应用。";
            return;
        }
        if (selected.ShowInWheel == showInWheel)
        {
            SaveStatusText.Text = showInWheel
                ? "所选应用已在轮盘中。"
                : "所选应用已在底部应用坞中。";
            return;
        }
        var index = _viewModel.Launchers.IndexOf(selected);
        if (index < 0)
            return;
        _viewModel.Launchers[index] = selected with
        {
            ShowInWheel = showInWheel
        };
        LauncherGrid.SelectedIndex = index;
        SaveStatusText.Text = showInWheel
            ? "已加入轮盘，点击“应用”后生效。"
            : "已移出轮盘，点击“应用”后生效。";
    }

    private void OnAddInputRuleClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "应用程序|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            AddRule(dialog.FileName);
    }

    private void OnAddCurrentAppClick(object sender, RoutedEventArgs e)
    {
        var path = _currentAppPath?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
        {
            SaveStatusText.Text = "无法读取当前前台应用路径。";
            return;
        }
        AddRule(path);
    }

    private void AddRule(string path)
    {
        if (_viewModel.InputRules.Any(rule =>
                string.Equals(rule.ExePath, path, StringComparison.OrdinalIgnoreCase)))
            return;
        _viewModel.InputRules.Add(new AppRule(
            Guid.NewGuid(),
            path,
            InputAction.LockEnglish,
            true));
    }

    private void OnRemoveInputRuleClick(object sender, RoutedEventArgs e)
    {
        if (InputRuleGrid.SelectedItem is AppRule selected)
            _viewModel.InputRules.Remove(selected);
    }

    private void OnAddSiteClick(object sender, RoutedEventArgs e)
    {
        var prompt = new PromptWindow(
            "添加 AI 网页",
            "名称",
            "例如：另一个 AI",
            "HTTPS 地址",
            "https://");
        if (prompt.ShowDialog() != true)
            return;
        if (!Uri.TryCreate(prompt.SecondValue, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            SaveStatusText.Text = "请输入有效的 HTTPS 地址。";
            return;
        }
        if (_viewModel.BrowserSites.Count >= 10)
        {
            SaveStatusText.Text = "最多添加 10 个网页。";
            return;
        }
        _viewModel.BrowserSites.Add(new BrowserSite(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(prompt.FirstValue) ? uri.Host : prompt.FirstValue.Trim(),
            uri,
            _viewModel.BrowserSites.Count));
    }

    private void OnRemoveSiteClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.BrowserSites.Count <= 1)
        {
            SaveStatusText.Text = "至少保留一个 AI 网页。";
            return;
        }
        if (BrowserSitesGrid.SelectedItem is BrowserSite selected)
            _viewModel.BrowserSites.Remove(selected);
    }

    private void OnEditSiteClick(object sender, RoutedEventArgs e)
    {
        if (BrowserSitesGrid.SelectedItem is not BrowserSite selected)
            return;
        var dialog = new PromptWindow(
            "编辑 AI 网页",
            "名称（1–40 字）",
            selected.Name,
            "HTTPS 地址",
            selected.Url.AbsoluteUri);
        if (dialog.ShowDialog() != true)
            return;
        var name = dialog.FirstValue.Trim();
        if (name.Length is < 1 or > 40 ||
            !Uri.TryCreate(dialog.SecondValue, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            SaveStatusText.Text = "网页名称必须为 1–40 字，地址必须使用 HTTPS。";
            return;
        }
        var index = _viewModel.BrowserSites.IndexOf(selected);
        _viewModel.BrowserSites[index] = selected with { Name = name, Url = uri };
    }

    private void OnEditLauncherClick(object sender, RoutedEventArgs e)
    {
        if (LauncherGrid.SelectedItem is not LauncherItem selected)
            return;
        var dialog = new LauncherEditWindow(selected)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;
        var index = _viewModel.Launchers.IndexOf(selected);
        _viewModel.Launchers[index] = selected with
        {
            Name = dialog.ItemName,
            Arguments = dialog.Arguments,
            WorkingDirectory = dialog.WorkingDirectory
        };
    }

    private void OnMoveLauncherUpClick(object sender, RoutedEventArgs e)
    {
        if (LauncherGrid.SelectedItem is not LauncherItem selected)
            return;
        var index = _viewModel.Launchers.IndexOf(selected);
        if (index > 0)
            _viewModel.Launchers.Move(index, index - 1);
    }

    private void OnMoveLauncherDownClick(object sender, RoutedEventArgs e)
    {
        if (LauncherGrid.SelectedItem is not LauncherItem selected)
            return;
        var index = _viewModel.Launchers.IndexOf(selected);
        if (index >= 0 && index < _viewModel.Launchers.Count - 1)
            _viewModel.Launchers.Move(index, index + 1);
    }

    private void OnChooseCaptureDirectoryClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(this) == true)
            _viewModel.CaptureSaveDirectory = dialog.FolderName;
    }

    private void OnExportSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "月语悬浮球设置|*.json",
            FileName = "YueYuOrb-settings.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(
                dialog.FileName,
                JsonSerializer.Serialize(_settings.Current, SettingsJsonOptions));
            SaveStatusText.Text = $"设置已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"导出失败：{exception.Message}";
        }
    }

    private async void OnImportSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "月语悬浮球设置|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var imported = SettingsMigrator.Deserialize(
                json,
                SettingsJsonOptions);
            var preview = MessageBox.Show(
                this,
                $"将导入 {imported.Launchers.Count} 个快捷项、{imported.BrowserSites.Count} 个网页和 {imported.InputLock.Rules.Count} 条输入规则。导入不会运行任何目标。继续吗？",
                "导入月语悬浮球设置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (preview != MessageBoxResult.Yes)
                return;
            var result = await _settings.UpdateAsync(
                current => imported with { Windows = current.Windows },
                CancellationToken.None);
            if (!result.Success)
            {
                SaveStatusText.Text = result.Message ?? "导入失败。";
                return;
            }
            StartupService.SetEnabled(imported.General.StartWithWindows);
            _viewModel = SettingsViewModel.From(_settings.Current);
            DataContext = _viewModel;
            Applied?.Invoke(_settings.Current);
            SaveStatusText.Text = "设置已导入并保存；快捷项未执行。";
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"导入被拒绝：{exception.Message}";
        }
    }

    private async void OnResetPreferencesClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "恢复默认偏好会重置悬浮球、规则、快捷项、网页与截图设置；不会删除笔记或已保存截图。继续吗？",
                "恢复默认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        var defaults = AppSettings.CreateDefault(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        var result = await _settings.UpdateAsync(
            current => defaults with { Windows = current.Windows },
            CancellationToken.None);
        if (!result.Success)
        {
            SaveStatusText.Text = result.Message ?? "恢复失败。";
            return;
        }
        StartupService.SetEnabled(false);
        _viewModel = SettingsViewModel.From(_settings.Current);
        DataContext = _viewModel;
        Applied?.Invoke(_settings.Current);
        SaveStatusText.Text = "已恢复默认偏好。";
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T element && element.Name == name)
                return element;
            var nested = FindVisualChild<T>(child, name);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}

internal sealed class LauncherEditWindow : Window
{
    private readonly System.Windows.Controls.TextBox _name;
    private readonly System.Windows.Controls.TextBox _arguments;
    private readonly System.Windows.Controls.TextBox _workingDirectory;

    public LauncherEditWindow(LauncherItem item)
    {
        Title = "编辑快捷应用";
        Width = 520;
        Height = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _name = new System.Windows.Controls.TextBox { Text = item.Name };
        _arguments = new System.Windows.Controls.TextBox
        {
            Text = string.Join(Environment.NewLine, item.Arguments),
            AcceptsReturn = true,
            Height = 100,
            VerticalScrollBarVisibility =
                System.Windows.Controls.ScrollBarVisibility.Auto,
            IsEnabled = item.Kind == LauncherKind.Exe
        };
        _workingDirectory = new System.Windows.Controls.TextBox
        {
            Text = item.WorkingDirectory ?? "",
            IsEnabled = item.Kind == LauncherKind.Exe
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "名称" });
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock
        {
            Text = "参数（每行一个，适用于 exe）",
            Margin = new Thickness(0, 10, 0, 0)
        });
        panel.Children.Add(_arguments);
        panel.Children.Add(new TextBlock
        {
            Text = "工作目录（适用于 exe）",
            Margin = new Thickness(0, 10, 0, 0)
        });
        panel.Children.Add(_workingDirectory);
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button
        {
            Content = "确定",
            Width = 76,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.Length > 80)
            {
                MessageBox.Show(this, "名称必须为 1–80 字。", "快捷应用");
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _name.Focus();
    }

    public string ItemName => _name.Text.Trim();
    public IReadOnlyList<string> Arguments => _arguments.Text
        .Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string? WorkingDirectory =>
        string.IsNullOrWhiteSpace(_workingDirectory.Text)
            ? null
            : _workingDirectory.Text.Trim();
}
