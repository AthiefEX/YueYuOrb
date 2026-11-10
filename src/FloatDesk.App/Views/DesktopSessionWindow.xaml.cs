using System.ComponentModel;
using System.Windows;
using FloatDesk.App.DesktopSessions;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FloatDesk.App.Views;

public partial class DesktopSessionWindow : Window
{
    private bool _allowClose;
    private bool _closingSession;
    private readonly IRdpControl _control;
    private Popup? _resolutionPopup;
    private int _desktopWidth = 1280;
    private int _desktopHeight = 720;
    public Func<Task<DesktopSessionActionStatus>>? StopSessionAsync { get; set; }
    public Func<int, int, Task>? SaveResolutionAsync { get; set; }

    public DesktopSessionWindow(
        IRdpControl control,
        string environmentName, int desktopWidth = 1280, int desktopHeight = 720)
    {
        ArgumentNullException.ThrowIfNull(control);
        InitializeComponent();
        GlassWindowFrame.Prepare(this);
        _control = control;
        FloatDesk.Core.Models.DesktopSessionResolutionPreferences.Validate(desktopWidth, desktopHeight);
        _desktopWidth = desktopWidth;
        _desktopHeight = desktopHeight;
        ApplyResolutionBounds();
        Loaded += (_, _) =>
        {
            ApplyResolutionBounds();
            Width = MaxWidth;
            Height = MaxHeight;
        };
        DpiChanged += (_, _) => ApplyResolutionBounds();
        Closed += (_, _) => { if (_resolutionPopup is not null) _resolutionPopup.IsOpen = false; };
        StateChanged += (_, _) => MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        EnvironmentNameText.Text = environmentName;
        Title = $"桌面分身 - {environmentName}";
        if (control is IRdpControlView controlView)
        {
            RdpHost.Child = controlView.View;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetStatus("当前控件不支持嵌入显示");
        }

        Closing += OnClosing;
    }

    public void SetStatus(string status) =>
        StatusText.Text = status;

    public void AllowClose() => _allowClose = true;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        await TryStopAndCloseAsync(() => SessionCloseConfirmation.Show(this));
    }

    public async Task<bool> TryStopAndCloseAsync(Func<bool> confirm)
    {
        if (_closingSession)
            return false;
        _closingSession = true;
        try
        {
            if (!confirm())
                return false;
            if (StopSessionAsync is null)
            {
                SetStatus("会话正在准备，暂不能安全清理，请稍后重试。");
                return false;
            }
            SetStatus("正在停止任务并注销分身，请稍候…");
            var result = await StopSessionAsync();
            if (!result.Success)
            {
                SetStatus(result.Message);
                return false;
            }
            _allowClose = true;
            if (IsVisible)
                Close();
            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"清理未完成，请重试：{exception.Message}");
            return false;
        }
        finally
        {
            _closingSession = false;
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (e.ClickCount == 2)
            OnMaximizeClick(sender, e);
        else
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnResolutionMenuClick(object sender, RoutedEventArgs e)
    {
        if (_resolutionPopup?.IsOpen == true)
        {
            _resolutionPopup.IsOpen = false;
            return;
        }
        var options = new StackPanel();
        options.Children.Add(new TextBlock { Text = "桌面分辨率", Margin = new Thickness(10, 5, 10, 9), FontWeight = FontWeights.SemiBold });
        foreach (var (width, height) in new[] { (1024, 768), (1280, 720), (1600, 900), (1920, 1080), (2560, 1440) })
        {
            var button = new Button
            {
                Content = $"{(_desktopWidth == width && _desktopHeight == height ? "✓ " : "   ")}{width} × {height}",
                Style = (Style)FindResource("ActionButtonStyle"), Margin = new Thickness(0, 2, 0, 2),
                HorizontalContentAlignment = HorizontalAlignment.Left, MinWidth = 175
            };
            button.Click += async (_, _) =>
            {
                _resolutionPopup!.IsOpen = false;
                ResolutionMenuButton.IsEnabled = false;
                try
                {
                    await UpdateResolutionAsync(width, height);
                    SetStatus($"{width} × {height} · 原始画面，可横向和竖向滚动");
                }
                catch (Exception exception) { SetStatus($"分辨率更新失败：{exception.Message}"); }
                finally { ResolutionMenuButton.IsEnabled = true; }
            };
            options.Children.Add(button);
        }
        var border = new Border { Child = options, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush");
        border.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "SettingsInkBrush");
        _resolutionPopup = new Popup { Child = border, PlacementTarget = ResolutionMenuButton,
            Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, IsOpen = true };
    }

    public async Task UpdateResolutionAsync(int width, int height)
    {
        if (_control is not IRdpDisplayControl display)
            throw new NotSupportedException("当前控件不支持动态分辨率。");
        await display.SetResolutionAsync(width, height, CancellationToken.None);
        _desktopWidth = width;
        _desktopHeight = height;
        ApplyResolutionBounds();
        Width = MaxWidth;
        Height = MaxHeight;
        if (SaveResolutionAsync is not null)
            await SaveResolutionAsync(width, height);
    }

    private void ApplyResolutionBounds()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
          var frameWidth = ActualWidth > 0 && RdpHost.ActualWidth > 0 ? ActualWidth - RdpHost.ActualWidth : 0;
          var frameHeight = ActualHeight > 0 && RdpHost.ActualHeight > 0 ? ActualHeight - RdpHost.ActualHeight : 80;
        MinWidth = Math.Min(480, _desktopWidth / dpi.DpiScaleX);
        MinHeight = Math.Min(320, _desktopHeight / dpi.DpiScaleY);
        MaxWidth = _desktopWidth / dpi.DpiScaleX + frameWidth;
        MaxHeight = _desktopHeight / dpi.DpiScaleY + frameHeight;
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
    }

}
