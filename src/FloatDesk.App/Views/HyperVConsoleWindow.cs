using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

/// <summary>Branded command surface around the existing VMConnect transport.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The HWND host is disposed by the owning Window.Closed handler.")]
internal sealed class HyperVConsoleWindow : Window
{
    private readonly Guid _vmId;
    private readonly Process _process;
    private readonly NativeConsoleStartupGuard _startupGuard;
    private readonly Grid _viewport = new();
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical };
    private readonly int _desktopWidth;
    private readonly int _desktopHeight;
    private readonly TextBlock _status = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Button _menu = new() { Content = "\u22EE", FontSize = 22 };
    private readonly TaskCompletionSource _attached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private VmConnectSurface? _surface;
    private Popup? _popup;
    private bool _busy;
    private bool _polling;
    private bool _closed;
    private bool _closePolicyRunning;
    private bool _closePolicyApplied;
    private bool _closePolicyFailed;
    private DateTime _statusHoldUntil;
    private TaskCompletionSource? _powerOperationCompletion;
    internal Task PowerOperationCompletion => _powerOperationCompletion?.Task ?? Task.CompletedTask;
    internal bool ShutdownCompleted { get; private set; }
    internal Func<string, Task<string>>? RunPowerScriptAsync { get; set; }
    /// <summary>Applies the environment's configured close action. Throws when the virtual
    /// machine did not reach the requested state.</summary>
    internal Func<Task<string>>? ApplyClosePolicyAsync { get; set; }
    /// <summary>Set by the owner while the application is shutting down: cancelling the pending
    /// close to save a virtual machine the user never asked to stop would be worse than leaving it
    /// running.</summary>
    internal bool SkipClosePolicy { get; set; }
    internal bool AllowClose { get; set; }
    internal Task Attached => _attached.Task;
    internal bool IsConsoleClosed => _closed;
    internal bool NativeStartupVisible { get; private set; }
    internal Func<int, int, Task>? SaveDisplayAsync { get; set; }
    internal Action? OpenSettings { get; set; }
    internal string? AuthenticationNotice { get; set; }
    internal string? NativeStartupError { get; set; }
    internal Func<IProgress<string>, Task<HyperVNetworkStatus>>? RepairNetworkAsync { get; set; }

    public HyperVConsoleWindow(Guid vmId, string name, Process process, int width, int height)
    {
        _vmId = vmId;
        DesktopSessionResolutionPreferences.Validate(width, height);
        _desktopWidth = width; _desktopHeight = height;
        _process = process;
        _startupGuard = new NativeConsoleStartupGuard(process.Id);
        Title = $"桌面分身 · {name}";
        Width = Math.Min(width, SystemParameters.WorkArea.Width);
        Height = Math.Min(height + 78, SystemParameters.WorkArea.Height);
        MinWidth = 560; MinHeight = 360;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "SettingsWindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "SettingsInkBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        { CaptionHeight = 48, ResizeBorderThickness = new Thickness(7), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var shell = new Border { Style = (Style)FindResource("ToolWindowShellStyle") };
        var grid = new Grid();
        grid.SetResourceReference(Panel.BackgroundProperty, "ToolWindowContentBrush");
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        var titleBar = new Grid { Style = (Style)FindResource("ToolWindowTitleBarStyle") };
        var bar = new DockPanel { LastChildFill = true };
        var captions = new StackPanel { Orientation = Orientation.Horizontal };
        WindowChrome.SetIsHitTestVisibleInChrome(captions, true);
        _menu.Style = (Style)FindResource("CaptionButtonStyle");
        _menu.ToolTip = "控制台设置";
        _menu.Click += (_, _) => OpenMenu();
        captions.Children.Add(_menu);
        captions.Children.Add(Caption("\uE921", "最小化", () => WindowState = WindowState.Minimized));
        captions.Children.Add(Caption("\uE8BB", "关闭控制台", Close, close: true));
        DockPanel.SetDock(captions, Dock.Right); bar.Children.Add(captions);
        _actions.Margin = new Thickness(10, 5, 0, 5);
        WindowChrome.SetIsHitTestVisibleInChrome(_actions, true);
        _actions.Children.Add(PowerAction("\uE7E8", "关机", "shutdown"));
        _actions.Children.Add(PowerAction("\uE74E", "保存状态", "save"));
        _actions.Children.Add(PowerAction("\uE768", "恢复／启动", "resume"));
        DockPanel.SetDock(_actions, Dock.Left); bar.Children.Add(_actions);
        bar.Children.Add(new TextBlock { Text = name, Margin = new Thickness(14, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        titleBar.Children.Add(bar);
        grid.Children.Add(titleBar);
        _viewport.ClipToBounds = true;
        _viewport.SetResourceReference(Panel.BackgroundProperty, "ToolWindowContentBrush");
        _viewport.RowDefinitions.Add(new RowDefinition());
        _viewport.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _viewport.ColumnDefinitions.Add(new ColumnDefinition());
        _viewport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _horizontal.Style = (Style)FindResource("SettingsScrollBarStyle");
        _horizontal.Height = 10; _horizontal.Width = double.NaN; _horizontal.MinWidth = 0;
        _vertical.Style = (Style)FindResource("SettingsScrollBarStyle");
        WindowChrome.SetIsHitTestVisibleInChrome(_horizontal,true);
        WindowChrome.SetIsHitTestVisibleInChrome(_vertical,true);
        _horizontal.Visibility = _vertical.Visibility = Visibility.Collapsed;
        Grid.SetRow(_horizontal, 1); Grid.SetColumn(_vertical, 1);
        _viewport.Children.Add(_horizontal); _viewport.Children.Add(_vertical);
        _horizontal.ValueChanged += (_, _) => PositionDesktop();
        _vertical.ValueChanged += (_, _) => PositionDesktop();
        _viewport.SizeChanged += (_, _) => UpdateDesktopLayout();
        Grid.SetRow(_viewport, 1); grid.Children.Add(_viewport);
        _status.Margin = new Thickness(16, 0, 16, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
        Grid.SetRow(_status, 2); grid.Children.Add(_status);
        shell.Child = grid; Content = shell;
        GlassWindowFrame.Prepare(this);
        ApplyResolutionBounds();
        Loaded += (_, _) => ApplyResolutionBounds();
        Loaded += async (_, _) => await AttachAsync();
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(BlockMaximize);
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };
        DpiChanged += (_, _) => { ApplyResolutionBounds(); UpdateDesktopLayout(); };
        Closing += (_, e) =>
        {
            if (_busy) { e.Cancel = true; _status.Text = "正在执行电源操作，请完成后再关闭控制台。"; return; }
            // The close policy used to run after the window was gone, driven by the console
            // process exit code. That exit code is not part of the contract: killing or detaching
            // the hosted client reports 1/-1, so a configured save-or-shutdown silently never ran.
            // It now runs here, while the window can still show what is happening, and it is
            // skipped only when the user already asked for that power operation or a previous try
            // failed (a second close is an explicit "close anyway").
            if (AllowClose || _closePolicyApplied || _closePolicyFailed || SkipClosePolicy || ApplyClosePolicyAsync is null)
                return;
            e.Cancel = true;
            if (!_closePolicyRunning) _ = RunClosePolicyAsync();
        };
        _timer.Tick += async (_, _) => await RefreshStateAsync();
        Closed += (_, _) =>
        {
            _closed = true; _timer.Stop();
            _startupGuard.Dispose();
            if (_popup is not null) _popup.IsOpen = false;
            _attached.TrySetCanceled();
            if (_surface is null && !_process.HasExited) _process.Kill();
            _surface?.CloseConsole();
            _surface?.Dispose();
        };
    }

    private static nint BlockMaximize(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if ((message == 0x0112 && ((long)wParam & 0xFFF0) == 0xF030) ||
            (message == 0x00A3 && wParam == 2)) handled = true;
        return 0;
    }

    private Button Caption(string glyph, string label, Action action, bool close = false)
    {
        var button = new Button { Style = (Style)FindResource(close ? "CloseCaptionButtonStyle" : "CaptionButtonStyle"),
            ToolTip = label, Content = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("SettingsIconFont") } };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }

    private Button Action(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Style = (Style)FindResource("ToolbarButtonStyle"), Margin = new Thickness(2, 0, 2, 0) };
        button.Click += async (_, _) => await action();
        return button;
    }

    private Button PowerAction(string glyph, string label, string operation)
    {
        var button = new Button
        {
            Style = (Style)FindResource("CaptionButtonStyle"),
            ToolTip = label,
            Content = new TextBlock { Text = glyph, FontSize = 15,
                FontFamily = (System.Windows.Media.FontFamily)FindResource("SettingsIconFont") }
        };
        ToolTipService.SetInitialShowDelay(button, 600);
        ToolTipService.SetShowDuration(button, 6000);
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await OperateAsync(operation);
        return button;
    }

    private async Task AttachAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            nint handle = 0;
            while (!_closed && !_process.HasExited && DateTime.UtcNow < deadline)
            {
                _process.Refresh();
                handle = VmConnectSurface.FindWindow(_process.Id);
                if (handle != 0) NativeConsoleStartupGuard.ConcealWindow(handle);
                if (handle != 0 && VmConnectSurface.TryGetDesktopPane(handle, out _)) break;
                _status.Text = "正在等待 VMConnect 初始化桌面控件…";
                await Task.Delay(100);
            }
            if (_closed) return;
            if (_process.HasExited) throw new InvalidOperationException($"连接客户端已退出（0x{_process.ExitCode:X8}）。{NativeStartupError}");
            if (handle == 0) throw new InvalidOperationException("VMConnect 未能打开连接窗口。");
            if (!VmConnectSurface.TryGetDesktopPane(handle, out _))
                throw new InvalidOperationException("VMConnect 主窗口已找到，但桌面控件初始化超时。虚拟机仍在运行，请关闭本窗口后重试。");
            if (VmConnectSurface.IsNativeVisible(handle) && !NativeConsoleStartupGuard.IsConcealed(handle)) NativeStartupVisible = true;
            _surface = new VmConnectSurface(handle, _desktopWidth, _desktopHeight);
            _viewport.Children.Add(_surface);
            _viewport.UpdateLayout();
            await _surface.Ready;
            _startupGuard.Reveal(handle);
            _surface.SizeChanged += (_, _) => UpdateDesktopLayout();
            ApplyResolutionBounds();
            UpdateDesktopLayout();
            _attached.TrySetResult();
            _timer.Start();
            await RefreshStateAsync();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            _status.ToolTip = exception.Message;
            _actions.IsEnabled = false;
            _startupGuard.Dispose();
            _surface?.Dispose();
            _viewport.Children.Clear();
            _viewport.Children.Add(new TextBlock
            {
                Text = "控制台连接尚未完成\n\n" + exception.Message,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            _attached.TrySetException(exception);
        }
    }

    private void ApplyResolutionBounds()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        // The glass shell has no outer inset; only caption/status consume space.
        MaxWidth = _desktopWidth / dpi.DpiScaleX;
        MaxHeight = _desktopHeight / dpi.DpiScaleY + 78;
        MinWidth = Math.Min(480, MaxWidth);
        MinHeight = Math.Min(320, MaxHeight);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

    private void UpdateDesktopLayout()
    {
        if (_surface is null) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var width = _viewport.ActualWidth;
        var height = _viewport.ActualHeight;
        var needHorizontal = _desktopWidth / dpi.DpiScaleX > width + 0.5;
        var needVertical = _desktopHeight / dpi.DpiScaleY > height + 0.5;
        needHorizontal |= needVertical && _desktopWidth / dpi.DpiScaleX > width - 10 + 0.5;
        needVertical |= needHorizontal && _desktopHeight / dpi.DpiScaleY > height - 10 + 0.5;
        _horizontal.Visibility = needHorizontal ? Visibility.Visible : Visibility.Collapsed;
        _vertical.Visibility = needVertical ? Visibility.Visible : Visibility.Collapsed;
        var viewWidth = Math.Max(0, width - (needVertical ? 10 : 0)) * dpi.DpiScaleX;
        var viewHeight = Math.Max(0, height - (needHorizontal ? 10 : 0)) * dpi.DpiScaleY;
        _horizontal.Maximum = Math.Max(0, _desktopWidth - viewWidth);
        _vertical.Maximum = Math.Max(0, _desktopHeight - viewHeight);
        _horizontal.ViewportSize = viewWidth; _vertical.ViewportSize = viewHeight;
        _horizontal.LargeChange = viewWidth; _vertical.LargeChange = viewHeight;
        PositionDesktop();
    }

    private void PositionDesktop() => _surface?.SetDesktopViewport(_desktopWidth, _desktopHeight,
        (int)_horizontal.Value, (int)_vertical.Value);

    private void OpenMenu()
    {
        if (_surface is null) return;
        if (_popup?.IsOpen == true) { _popup.IsOpen = false; return; }
        var options = new StackPanel();
        if (RepairNetworkAsync is not null) options.Children.Add(Action("修复网络", async () =>
        {
            if (_popup is not null) _popup.IsOpen = false;
            if (_busy) return;
            _busy = true; _actions.IsEnabled = false;
            try
            {
                var result = await RepairNetworkAsync(new Progress<string>(message => _status.Text = message));
                _status.Text = result.Summary; _status.ToolTip = result.Summary;
            }
            catch (Exception exception) { _status.Text = exception.Message; _status.ToolTip = exception.Message; }
            finally { _statusHoldUntil = DateTime.UtcNow.AddSeconds(20); _busy = false; _actions.IsEnabled = true; }
        }));
        var heading = new TextBlock { Text = "分辨率 · 下次连接生效", Margin = new Thickness(8, 5, 8, 7) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
        options.Children.Add(heading);
        foreach (var (width, height) in new[] { (1024, 768), (1280, 720), (1600, 900), (1920, 1080), (2560, 1440) })
        {
            var button = Action($"{width} × {height}", async () =>
            {
                if (_popup is not null) _popup.IsOpen = false;
                try
                {
                    if (SaveDisplayAsync is null) throw new InvalidOperationException("当前入口未提供分辨率设置。");
                    await SaveDisplayAsync(width, height);
                    _status.Text = $"已保存 {width} × {height}，下次打开控制台生效。";
                }
                catch (Exception exception) { _status.Text = exception.Message; }
            });
            options.Children.Add(button);
        }
        options.Children.Add(Action(Topmost ? "取消窗口置顶" : "窗口置顶", () =>
        {
            Topmost = !Topmost;
            if (_popup is not null) _popup.IsOpen = false;
            return Task.CompletedTask;
        }));
        options.Children.Add(Action("刷新连接画面", () =>
        {
            _surface.Refresh();
            if (_popup is not null) _popup.IsOpen = false;
            return Task.CompletedTask;
        }));
        if (OpenSettings is not null) options.Children.Add(Action("更多分身设置", () =>
        {
            if (_popup is not null) _popup.IsOpen = false;
            OpenSettings();
            return Task.CompletedTask;
        }));
        var border = new Border { Child = options, Width = 230, Padding = new Thickness(8), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        border.SetValue(System.Windows.Documents.TextElement.FontSizeProperty, 12.0);
        border.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush");
        _popup = new Popup { Child = border, PlacementTarget = _menu, Placement = PlacementMode.Bottom,
            HorizontalOffset = _menu.ActualWidth - 230, StaysOpen = false, AllowsTransparency = true };
        _popup.IsOpen = true;
    }

    private async Task OperateAsync(string operation)
    {
        if (_busy) return;
        _busy = true; _actions.IsEnabled = false;
        _powerOperationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _status.Text = "正在执行，请稍候…";
        _status.ToolTip = _status.Text;
        var closeAfter = false;
        try
        {
            if (operation == "save" && RunPowerScriptAsync is null)
            {
                await new HyperVVirtualMachineManager()
                    .ApplyCloseActionAsync(_vmId, HyperVConsoleCloseAction.SaveState, System.Threading.CancellationToken.None);
            }
            else
            {
                var script = operation == "save"
                    ? HyperVConsoleSettings.BuildCloseScript(_vmId, HyperVConsoleCloseAction.SaveState)
                    : $$"""
                        $ErrorActionPreference='Stop'
                        Import-Module Hyper-V
                        $vm=Get-VM -Id ([guid]'{{_vmId:D}}') -ErrorAction Stop
                        {{(operation == "shutdown"
                            ? "if ($vm.State -eq 'Running') { Stop-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop } elseif ($vm.State -ne 'Off') { throw '请先恢复来宾，再正常关机。' }; if ((Get-VM -Id $vm.Id -ErrorAction Stop).State -ne 'Off') { throw 'ShutdownReadbackMismatch' }"
                            : "if ($vm.State -eq 'Paused') { Resume-VM -VM $vm -Confirm:$false -ErrorAction Stop } elseif ($vm.State -eq 'Saved' -or $vm.State -eq 'Off') { Start-VM -VM $vm -Confirm:$false -ErrorAction Stop }")}}
                        'ok'
                        """;
                if (RunPowerScriptAsync is { } run) await run(script);
                else await new PowerShellHyperVHost().RunScriptForManagementAsyncCore(script, System.Threading.CancellationToken.None);
            }
            if (operation == "shutdown")
            {
                ShutdownCompleted = true;
                _closePolicyApplied = true;
                closeAfter = true;
            }
            else if (operation == "save")
            {
                // The virtual machine is saved: its memory is on disk and the console has nothing
                // left to show. Keeping a frozen frame open is what made this button read as
                // "no effect", and it also meant closing afterwards would run the policy again.
                _closePolicyApplied = true;
                _status.Text = "已保存虚拟机状态并释放运行资源。";
                _status.ToolTip = _status.Text;
                closeAfter = true;
                // Briefly hold the confirmation before the window disappears.
                await Task.Delay(900);
            }
        }
        catch (Exception exception) { _status.Text = $"操作未完成：{exception.Message}"; _status.ToolTip = _status.Text; }
        finally { _busy = false; _actions.IsEnabled = true; _powerOperationCompletion.TrySetResult(); }
        if (closeAfter && !_closed)
        {
            AllowClose = true;
            Close();
        }
    }

    /// <summary>
    /// Runs the environment's close action while the console is still usable, so a failure is
    /// visible and retryable instead of disappearing with the window.
    /// </summary>
    private async Task RunClosePolicyAsync()
    {
        _closePolicyRunning = true;
        _busy = true;
        _actions.IsEnabled = false;
        _status.Text = "正在按关闭设置处理虚拟机，请稍候…";
        _status.ToolTip = _status.Text;
        try
        {
            var message = await ApplyClosePolicyAsync!();
            _closePolicyApplied = true;
            _status.Text = message;
            _status.ToolTip = message;
            _statusHoldUntil = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception exception)
        {
            _closePolicyFailed = true;
            var message = $"关闭设置未完成：{exception.Message}。虚拟机保持当前状态，再次点击关闭只会关闭控制台。";
            _status.Text = message;
            _status.ToolTip = message;
            _statusHoldUntil = DateTime.UtcNow.AddSeconds(30);
        }
        finally
        {
            _busy = false;
            _actions.IsEnabled = true;
            _closePolicyRunning = false;
        }
        if (_closePolicyApplied && !_closed)
        {
            AllowClose = true;
            // A policy that completes immediately would otherwise call Close from inside the
            // Closing handler that started it, which WPF ignores: the window stayed open looking
            // like the close had failed. Queue the close instead.
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Normal);
        }
    }

    private async Task RefreshStateAsync()
    {
        if (_closed || _polling || _busy || DateTime.UtcNow < _statusHoldUntil) return;
        _polling = true;
        try
        {
            if (_process.HasExited)
            {
                _timer.Stop();
                if (_process.ExitCode == 0) Close();
                else _status.Text = $"远程桌面连接组件已退出（0x{_process.ExitCode:X8}）。虚拟机未关闭，请重新打开控制台。";
                return;
            }
            if (AuthenticationNotice is { } notice) { _status.Text = notice; _status.ToolTip = notice; return; }
            var machines = await new HyperVVirtualMachineManager().ListAsync(System.Threading.CancellationToken.None);
            var vm = System.Linq.Enumerable.FirstOrDefault(machines, item => item.Id == _vmId);
            if (vm is not null) _status.Text = vm.DisplayStatus;
        }
        catch (Exception exception) { _status.Text = $"状态读取失败：{exception.Message}"; }
        finally { _polling = false; }
    }
}
