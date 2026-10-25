using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow : Window
{
    public event Action<LauncherItem>? LaunchRequested;
    public event Action? SettingsRequested;
    public event Action? InputLockToggleRequested;
    public event Action? AiRequested;
    public event Action? NotesRequested;
    public event Action<FloatDesk.Core.Models.CaptureMode>? CaptureRequested;

    private bool _allowDeactivateClose = true;

    public QuickPanelWindow()
    {
        InitializeComponent();
    }

    public void Apply(AppSettings settings, bool manualLockEnabled, string inputStatus)
    {
        AutoRuleText.Text = settings.InputLock.AutoEnabled ? "自动规则：开" : "自动规则：关";
        InputLockButton.Content = manualLockEnabled ? "停止锁定" : "锁定英文";
        InputLockButton.Background = manualLockEnabled
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("SurfaceRaisedBrush");
        InputLockButton.Foreground = manualLockEnabled ? Brushes.White : (Brush)FindResource("InkBrush");
        InputStatusText.Text = $"输入状态：{inputStatus}";

        LauncherPanel.Children.Clear();
        foreach (var item in settings.Launchers.OrderBy(item => item.Order))
        {
            var button = new Button
            {
                Content = item.Name,
                Width = 96,
                Height = 58,
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = item.Path
            };
            button.Click += (_, _) => LaunchRequested?.Invoke(item);
            LauncherPanel.Children.Add(button);
        }

        var addButton = new Button
        {
            Content = "+ 添加应用",
            Width = 96,
            Height = 58,
            Margin = new Thickness(0, 0, 6, 6)
        };
        addButton.Click += (_, _) => SettingsRequested?.Invoke();
        LauncherPanel.Children.Add(addButton);
    }

    public void SetRegionCaptureHotkey(string hotkey)
    {
        RegionCaptureButton.ToolTip = $"框选截图（{hotkey}）";
    }

    public void ShowNear(Window orb, PixelRect orbPixels)
    {
        ApplySizeToContent();
        if (!IsVisible)
            Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        var pixels = GetWindowRect(handle, out var current)
            ? new PixelRect(
                current.Left,
                current.Top,
                Math.Max(1, current.Right - current.Left),
                Math.Max(1, current.Bottom - current.Top))
            : new PixelRect(
                (int)Left,
                (int)Top,
                Math.Max(1, (int)ActualWidth),
                Math.Max(1, (int)ActualHeight));
        var monitors = DisplayTopology.GetMonitors();
        var monitor = DisplayTopology.FromPoint(orbPixels.Location)
            ?? (monitors.Count > 0 ? monitors[0] : null);
        var work = monitor?.WorkArea ?? new PixelRect(
            0,
            0,
            GetSystemMetrics(0),
            GetSystemMetrics(1));
        var point = OrbPlacement.PlaceQuickPanel(orbPixels, pixels.Size, work);
        SetWindowPos(handle, 0, point.X, point.Y, pixels.Width, pixels.Height, 0x0004 | 0x0010);
        Activate();
        Focus();
    }

    public void RefreshVisibility() => ShowNear(Owner ?? this, new PixelRect(
        (int)Left,
        (int)Top,
        (int)Width,
        (int)Height));

    private void ApplySizeToContent()
    {
        SizeToContent = SizeToContent.Height;
        Width = 340;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_allowDeactivateClose)
            return;
        Hide();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void OnInputLockClick(object sender, RoutedEventArgs e) => InputLockToggleRequested?.Invoke();
    private void OnAiClick(object sender, RoutedEventArgs e) => AiRequested?.Invoke();
    private void OnNotesClick(object sender, RoutedEventArgs e) => NotesRequested?.Invoke();
    private void OnRegionCaptureClick(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.Region);
    private void OnWindowCaptureClick(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.ActiveWindow);
    private void OnMonitorCaptureClick(object sender, RoutedEventArgs e) => CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.CurrentMonitor);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint hwnd,
        out WindowRect rect);

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
