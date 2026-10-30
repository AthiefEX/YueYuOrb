using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using FloatDesk.App.Brightness;
using FloatDesk.Core.Brightness;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public sealed class BrightnessControlWindow : Window, IDisposable
{
    private readonly IBrightnessService _service;
    private readonly NightModeController _night;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Slider _brightness = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _level = new();
    private readonly TextBlock _devices = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _toggle = new() { Content = "黑夜模式", Margin = new Thickness(0, 12, 0, 8) };
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private bool _updating, _busy, _closed;
    private int? _pending;
    public WindowPlacement? RememberedPlacement { get; set; }
    private bool _userMoved;
    public BrightnessControlWindow(IBrightnessService service, NightModeController night, BrightnessSettings settings)
    {
        _service = service; _night = night;
        Title = "亮度调节"; Width = 360; SizeToContent = SizeToContent.Height; MaxHeight = 620;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/BrightnessControlResources.xaml", UriKind.Relative) });
        var body = new StackPanel { Margin = new Thickness(20) };
        body.SetResourceReference(TextElement.ForegroundProperty, "SettingsInkBrush");
        _brightness.Style = (Style)FindResource("BrightnessSliderStyle");
        _brightness.Margin = new Thickness(0, 8, 0, 4);
        _toggle.Style = (Style)FindResource("NightModePillStyle");
        _toggle.HorizontalAlignment = HorizontalAlignment.Stretch;
        _toggle.Margin = new Thickness(0, 18, 0, 14);
        _level.FontSize = 16; _level.FontWeight = FontWeights.SemiBold;
        _level.Margin = new Thickness(0, 18, 0, 0);
        _devices.FontSize = 12; _devices.LineHeight = 19;
        _devices.SetResourceReference(TextBlock.ForegroundProperty, "SettingsMutedBrush");
        _status.FontSize = 12; _status.Margin = new Thickness(0, 10, 0, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "SettingsAccentBrush");
        var title = new DockPanel();
        title.Background = System.Windows.Media.Brushes.Transparent;
        PopupWindowInteraction.EnableDragging(this, title, () => _userMoved = true);
        _ = new OutsideClickDismissBehavior(this, Close);
        var close = new Button { Content = "×", Width = 30, Height = 30, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), Style = (Style)FindResource("ActionButtonStyle") };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭亮度面板");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = "所有屏幕亮度", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(title); body.Children.Add(_level); body.Children.Add(_brightness);
        var deviceCard = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 4, 0, 0), Child = new ScrollViewer { Content = _devices, MaxHeight = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        deviceCard.SetResourceReference(Border.BackgroundProperty, "SettingsCardSubtleBrush");
        body.Children.Add(deviceCard);
        body.Children.Add(_toggle);
        var nightLevel = new TextBlock { Text = $"降暗程度 {settings.NightDimmingPercent}%" };
        body.Children.Add(nightLevel);
        var dim = new Slider { Minimum = 0, Maximum = 90, TickFrequency = 1, IsSnapToTickEnabled = true,
            Value = settings.NightDimmingPercent, Style = (Style)FindResource("BrightnessSliderStyle"), Margin = new Thickness(0, 6, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(dim, "黑夜降暗程度");
        dim.ValueChanged += (_, _) => { nightLevel.Text = $"降暗程度 {Math.Round(dim.Value)}%"; _night.SetDimmingPercent((int)dim.Value); };
        body.Children.Add(dim);
        var recover = new Button { Content = "恢复可见亮度", Margin = new Thickness(0, 18, 0, 0),
            Style = (Style)FindResource("ActionButtonStyle"), MinHeight = 40 };
        recover.Click += async (_, _) => { _night.SetEnabled(false); await ChangeAllAsync(50); };
        body.Children.Add(recover); body.Children.Add(_status);
        var card = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush"); Content = card;
        System.Windows.Automation.AutomationProperties.SetName(_brightness, "所有屏幕亮度");
        _brightness.ValueChanged += (_, _) =>
        {
            if (_updating) return;
            _level.Text = $"亮度 {Math.Round(_brightness.Value)}%";
            _pending = (int)_brightness.Value;
            _delay.Stop(); _delay.Start();
        };
        _delay.Tick += async (_, _) =>
        {
            _delay.Stop();
            if (_busy) { _delay.Start(); return; }
            if (_pending is not { } value) return;
            _pending = null; await ChangeAllAsync(value);
        };
        _toggle.Checked += (_, _) => { if (!_updating) _night.SetEnabled(true); };
        _toggle.Unchecked += (_, _) => { if (!_updating) _night.SetEnabled(false); };
        _night.Changed += OnNightChanged;
        SizeChanged += (_, _) => QueueClamp();
        DpiChanged += (_, _) => QueueClamp();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => { OnNightChanged(); await RefreshAsync(); PositionNearCursor(); _night.RaiseControls(); };
        Closed += (_, _) => { _closed = true; _delay.Stop(); _night.Changed -= OnNightChanged; Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged; _lifetime.Cancel(); _lifetime.Dispose(); };
    }
    private void OnNightChanged()
    {
        _updating = true;
        try { _toggle.IsChecked = _night.IsEnabled; _toggle.Content = _night.IsEnabled ? "☾  黑夜模式 · 已开启" : "☾  黑夜模式 · 已关闭"; }
        finally { _updating = false; }
    }
    private async Task RefreshAsync()
    {
        _brightness.IsEnabled = false;
        try
        {
            var snapshot = await _service.GetSnapshotAsync(_lifetime.Token);
            if (_closed) return;
            Display(snapshot);
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) _status.Text = "无法读取亮度，请稍后重新打开面板。黑夜模式仍可使用。"; }
    }
    private void Display(BrightnessSnapshot snapshot)
    {
        _updating = true;
        try
        {
            var supported = snapshot.Devices.Where(d => d.Percent.HasValue && d.UnavailableReason is null).ToArray();
            _brightness.IsEnabled = supported.Length > 0;
            if (_pending is null && supported.Length > 0)
            {
                _brightness.Value = (supported.FirstOrDefault(d => d.IsPrimary) ?? supported[0]).Percent!.Value;
                _level.Text = supported.Select(d => d.Percent).Distinct().Count() > 1 ? "各屏幕亮度不同" : $"亮度 {_brightness.Value}%";
            }
            _devices.Text = string.Join("\n", snapshot.Devices.Select(d => $"{d.Name}：{(d.UnavailableReason ?? (d.Percent is { } p ? p + "%" : "无法读取"))}"));
            if (snapshot.Devices.Count == 0) _devices.Text = "未发现可调节的显示器。";
        }
        finally { _updating = false; }
    }
    private async Task ChangeAllAsync(int value)
    {
        if (_busy || _closed) { _pending = value; return; }
        _busy = true;
        try
        {
            var snapshot = await _service.GetSnapshotAsync(_lifetime.Token);
            var targets = BrightnessPolicy.CreateTargets(snapshot, value, false);
            var results = await Task.WhenAll(targets.Select(pair => _service.SetPercentAsync(pair.Key, pair.Value, _lifetime.Token)));
            if (_closed) return;
            _status.Text = targets.Count == 0 ? "没有可调节的屏幕。" : string.Join("\n", results.Where(r => r.Error is not null).Select(r => r.Error));
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) _status.Text = "亮度调节失败，请稍后重试。"; }
        finally { _busy = false; if (!_closed && _pending.HasValue) _delay.Start(); }
    }
    public void PositionNearCursor()
    {
        if (_userMoved) { QueueClamp(); return; }
        if (PopupWindowInteraction.TryRestore(this, RememberedPlacement)) { QueueClamp(); return; }
        UpdateLayout();
        var point = System.Windows.Forms.Cursor.Position;
        var bounds = System.Windows.Forms.Screen.FromPoint(point).WorkingArea;
        var source = System.Windows.PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        var width = (int)Math.Ceiling(ActualWidth * scale.M11);
        var height = (int)Math.Ceiling(ActualHeight * scale.M22);
        SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0,
            Math.Clamp(point.X + 12, bounds.Left, Math.Max(bounds.Left, bounds.Right - width)),
            Math.Clamp(point.Y + 12, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height)), 0, 0, 0x15);
        QueueClamp();
    }
    private bool _clampQueued;
    private void OnDisplayChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(QueueClamp);
    private void QueueClamp()
    {
        if (_closed || !IsVisible || _clampQueued) return;
        _clampQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _clampQueued = false;
            if (_closed || !IsVisible) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var bounds = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            MaxHeight = Math.Min(620, bounds.Height / dpi.DpiScaleY);
            MaxWidth = bounds.Width / dpi.DpiScaleX;
            UpdateLayout();
            if (!GetWindowRect(handle, out var rect)) return;
            var x = Math.Clamp(rect.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - (rect.Right - rect.Left)));
            var y = Math.Clamp(rect.Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - (rect.Bottom - rect.Top)));
            if (x != rect.Left || y != rect.Top) SetWindowPos(handle, 0, x, y, 0, 0, 0x15);
        }));
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    public void Dispose() { if (!_closed) Close(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
