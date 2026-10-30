using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using FloatDesk.Windows.Capture;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public sealed class AudioControlWindow : Window, IDisposable
{
    private readonly IAudioService _audio;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StackPanel _devices = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly Button _mute = new() { Content = "静音", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _level = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _volumeDelay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private AudioSnapshot? _snapshot;
    private bool _updating;
    private bool _busy;
    private bool _closed;
    private string? _pendingDevice;
    private double _pendingVolume;
    public WindowPlacement? RememberedPlacement { get; set; }
    private bool _userMoved;

    public AudioControlWindow(IAudioService audio)
    {
        _audio = audio;
        Title = "播放设备";
        Width = 340; SizeToContent = SizeToContent.Height; MaxHeight = 620;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        var body = new StackPanel { Margin = new Thickness(16) };
        var title = new DockPanel();
        title.Background = Brushes.Transparent;
        PopupWindowInteraction.EnableDragging(this, title, () => _userMoved = true);
        _ = new OutsideClickDismissBehavior(this, Close);
        var close = new Button { Content = "×", Width = 28, Height = 28, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭音频面板");
        System.Windows.Automation.AutomationProperties.SetName(_volume, "当前播放设备音量");
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        close.Content = Label("×");
        title.Children.Add(new TextBlock { Text = "播放设备", FontSize = 18, FontWeight = FontWeights.SemiBold });
        body.Children.Add(title);
        body.Children.Add(new ScrollViewer { Content = _devices, MaxHeight = 350, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 12) });
        body.Children.Add(_level); body.Children.Add(_volume); body.Children.Add(_mute); body.Children.Add(_status);
        var card = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Child = body };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush");
        body.SetResourceReference(TextElement.ForegroundProperty, "SettingsInkBrush");
        Content = card;
        _volume.ValueChanged += (_, _) =>
        {
            _level.Text = $"音量 {Math.Round(_volume.Value)}%";
            if (_updating || _busy || _snapshot?.DefaultDeviceId is not { } id) return;
            _pendingDevice = id; _pendingVolume = _volume.Value / 100;
            _volumeDelay.Stop(); _volumeDelay.Start();
        };
        _volumeDelay.Tick += async (_, _) =>
        {
            _volumeDelay.Stop();
            if (_busy) { _volumeDelay.Start(); return; }
            if (_pendingDevice is not { } id) return;
            var value = _pendingVolume; _pendingDevice = null;
            await PerformAsync(() => _audio.SetVolumeAsync(id, value, _lifetime.Token));
        };
        _mute.Click += async (_, _) =>
        {
            if (_snapshot?.DefaultDeviceId is { } id)
                await PerformAsync(() => _audio.SetMuteAsync(id, !_snapshot.Muted, _lifetime.Token));
        };
        _refresh.Tick += async (_, _) => await RefreshAsync();
        SizeChanged += (_, _) => { if (IsVisible) ClampToWorkArea(); };
        Loaded += async (_, _) =>
        {
            _refresh.Start(); await RefreshAsync();
            if (!_closed && !_userMoved && RememberedPlacement is not null)
                PopupWindowInteraction.TryRestore(this, RememberedPlacement);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { _closed = true; _refresh.Stop(); _volumeDelay.Stop(); _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (_busy || _closed) return;
        _busy = true; _devices.IsEnabled = false; _volume.IsEnabled = false; _mute.IsEnabled = false;
        try { await action(); _status.Text = ""; }
        catch (OperationCanceledException) { }
        catch { if (!_closed) _status.Text = "音频操作失败，请刷新设备或稍后重试。"; }
        finally { _busy = false; if (!_closed) { _devices.IsEnabled = true; await RefreshAsync(); } }
    }

    public void Dispose()
    {
        if (!_closed) Close();
        GC.SuppressFinalize(this);
    }

    public void PositionNearCursor()
    {
        if (_userMoved) return;
        if (PopupWindowInteraction.TryRestore(this, RememberedPlacement)) return;
        var handle = new WindowInteropHelper(this).Handle;
        var pointer = System.Windows.Forms.Cursor.Position;
        var work = System.Windows.Forms.Screen.FromPoint(pointer).WorkingArea;
        for (var pass = 0; pass < 2; pass++)
        {
            var bounds = DesktopGeometry.GetVisibleWindowBounds(handle);
            SetWindowPos(handle, 0,
                Math.Clamp(pointer.X, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
                Math.Clamp(pointer.Y, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)),
                0, 0, 0x0001 | 0x0004 | 0x0010);
        }
    }

    private void ClampToWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var bounds = DesktopGeometry.GetVisibleWindowBounds(handle);
        var work = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        SetWindowPos(handle, 0, Math.Clamp(bounds.Left, work.Left, Math.Max(work.Left, work.Right - bounds.Width)),
            Math.Clamp(bounds.Top, work.Top, Math.Max(work.Top, work.Bottom - bounds.Height)), 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsInkBrush");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "SettingsBodyFont");
        return label;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);

    private async Task RefreshAsync()
    {
        if (_busy || _closed || _volume.IsMouseCaptureWithin || _pendingDevice is not null) return;
        _busy = true;
        _devices.IsEnabled = false; _volume.IsEnabled = false; _mute.IsEnabled = false;
        try
        {
            var snapshot = await _audio.GetSnapshotAsync(_lifetime.Token);
            if (_closed) return;
            _snapshot = snapshot;
            _devices.Children.Clear();
            foreach (var device in snapshot.Devices)
            {
                var button = new Button { Content = Label((device.Id == snapshot.DefaultDeviceId ? "✓  " : "    ") + device.Name),
                    HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3),
                    Padding = new Thickness(10, 8, 10, 8), ToolTip = device.Name };
                button.SetResourceReference(StyleProperty, "ToolbarButtonStyle");
                button.Click += async (_, _) => await PerformAsync(() => _audio.SetDefaultDeviceAsync(device.Id, _lifetime.Token));
                _devices.Children.Add(button);
            }
            _updating = true; _volume.Value = snapshot.Volume * 100; _updating = false;
            _mute.Content = Label(snapshot.Muted ? "取消静音" : "静音");
            _mute.IsEnabled = _volume.IsEnabled = snapshot.DefaultDeviceId is not null;
            if (snapshot.Devices.Count == 0) _status.Text = "未找到可用的播放设备。";
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) _status.Text = "无法读取播放设备，请稍后重试。"; }
        finally
        {
            _busy = false;
            if (!_closed)
            {
                _devices.IsEnabled = true;
                _mute.IsEnabled = _volume.IsEnabled = _snapshot?.DefaultDeviceId is not null;
                UpdateLayout(); ClampToWorkArea();
            }
        }
    }
}
