using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class OrbWindow : Window
{
    private const double OrbChromePadding = 20;
    private readonly DispatcherTimer _clockTimer = new();
    private readonly Action _openPanel;
    private readonly Action<PixelRect> _positionChanged;
    private readonly Action _toggleVisibility;
    private readonly Action _exit;
    private readonly string _orbImagePath;
    private AppSettings? _settings;
    private System.Windows.Point _pointerDown;
    private System.Windows.Point _windowDown;
    private bool _dragging;
    private double _sizeDip = 56;
    private bool _snap = true;
    private bool _positionLocked;
    private OrbImageShape _imageShape;
    private byte[]? _imageBgra;
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private int _imageStride;

    public OrbWindow(
        Action openPanel,
        Action<PixelRect> positionChanged,
        Action settingsRequested,
        Action aiRequested,
        Action notesRequested,
        Action captureRequested,
        Action toggleVisibility,
        Action exit,
        string? orbImagePath = null)
    {
        InitializeComponent();
        _openPanel = openPanel;
        _positionChanged = positionChanged;
        _toggleVisibility = toggleVisibility;
        _exit = exit;
        _orbImagePath = orbImagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatDesk",
            "assets",
            "orb.png");
        OrbRoot.MouseLeftButtonDown += OnMouseLeftButtonDown;
        OrbRoot.MouseMove += OnMouseMove;
        OrbRoot.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _clockTimer.Tick += (_, _) => RefreshClock();
        SourceInitialized += (_, _) =>
        {
            SetNonActivatingStyle();
            HwndSource.FromHwnd(
                new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        var menu = new System.Windows.Controls.ContextMenu();
        AddMenu(menu, "打开快捷面板", _openPanel);
        AddMenu(menu, "设置", settingsRequested);
        AddMenu(menu, "AI 网页", aiRequested);
        AddMenu(menu, "记事本", notesRequested);
        AddMenu(menu, "框选截图", captureRequested);
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddMenu(menu, "显示/隐藏悬浮球", _toggleVisibility);
        AddMenu(menu, "退出", _exit);
        OrbRoot.ContextMenu = menu;
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        _sizeDip = settings.Orb.SizeDip;
        _snap = settings.Orb.SnapToEdge;
        _positionLocked = settings.Orb.PositionLocked;
        _imageShape = settings.Orb.ImageShape;
        OrbLayout.Margin = new Thickness(OrbChromePadding);
        Width = _sizeDip + (OrbChromePadding * 2);
        OrbRoot.Width = _sizeDip;
        OrbRoot.Height = _sizeDip;
        Opacity = settings.Orb.Opacity;
        OrbRoot.Clip = null;
        OrbImage.Clip = settings.Orb.ImageShape == OrbImageShape.Circle
            ? new System.Windows.Media.EllipseGeometry(
                new System.Windows.Point(_sizeDip / 2, _sizeDip / 2),
                _sizeDip / 2,
                _sizeDip / 2)
            : null;
        ClockBorder.Visibility = settings.Orb.ShowClock
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (settings.Orb.ShowClock)
        {
            RefreshClock(settings);
            ScheduleClockTick(settings);
        }
        else
        {
            _clockTimer.Stop();
        }

        var bitmap = TryLoadBitmap(_orbImagePath);
        OrbImage.Source = bitmap;
        OrbImage.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        OrbCircle.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        FallbackText.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        CaptureAlpha(bitmap);
    }

    public void SetTimeFormat(AppSettings settings) => RefreshClock(settings);

    public void SetPosition(PixelRect bounds)
    {
        if (PresentationSource.FromVisual(this) is null)
            return;
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(
            handle,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            0x0004 | 0x0010);
    }

    public void ApplyOverlay(bool visible, bool topmost)
    {
        if (visible)
        {
            if (!IsVisible)
                Show();
            Topmost = topmost;
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowPos(
                handle,
                topmost ? new nint(-1) : new nint(-2),
                0,
                0,
                0,
                0,
                0x0001 | 0x0002 | 0x0010);
        }
        else
            Hide();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pointerDown = e.GetPosition(this);
        _windowDown = new System.Windows.Point(Left, Top);
        _dragging = false;
        OrbRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!OrbRoot.IsMouseCaptured || _positionLocked)
            return;
        var pointer = e.GetPosition(this);
        if (!_dragging &&
            Math.Abs(pointer.X - _pointerDown.X) < 3 &&
            Math.Abs(pointer.Y - _pointerDown.Y) < 3)
            return;
        _dragging = true;
        Left = _windowDown.X + pointer.X - _pointerDown.X;
        Top = _windowDown.Y + pointer.Y - _pointerDown.Y;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!OrbRoot.IsMouseCaptured)
            return;
        OrbRoot.ReleaseMouseCapture();
        if (_dragging)
        {
            SnapIfNeeded();
            _positionChanged(GetPhysicalBounds());
        }
        else
        {
            _openPanel();
        }
        e.Handled = true;
    }

    private void SnapIfNeeded()
    {
        if (!_snap)
            return;
        var bounds = GetPhysicalBounds();
        var monitor = DisplayTopology.FromPoint(bounds.Location);
        if (monitor is null)
            return;
        var scale = DesktopGeometry.GetWindowDpiScale(
            new WindowInteropHelper(this).Handle);
        var threshold = Math.Max(1, (int)Math.Round(16 * scale));
        var left = bounds.Left;
        var top = bounds.Top;
        if (Math.Abs(left - monitor.WorkArea.Left) < threshold)
            left = monitor.WorkArea.Left;
        if (Math.Abs(monitor.WorkArea.Right - bounds.Right) < threshold)
            left = monitor.WorkArea.Right - bounds.Width;
        if (Math.Abs(top - monitor.WorkArea.Top) < threshold)
            top = monitor.WorkArea.Top;
        if (Math.Abs(monitor.WorkArea.Bottom - bounds.Bottom) < threshold)
            top = monitor.WorkArea.Bottom - bounds.Height;
        SetPosition(OrbPlacement.Clamp(
            new PixelRect(left, top, bounds.Width, bounds.Height),
            monitor.WorkArea));
    }

    private void RefreshClock(AppSettings? settings = null)
    {
        settings ??= _settings;
        var current = DateTime.Now;
        var use24 = settings?.Orb.Clock24Hour ?? true;
        var seconds = settings?.Orb.ShowSeconds ?? false;
        ClockText.Text = current.ToString(
            use24
                ? seconds ? "HH:mm:ss" : "HH:mm"
                : seconds ? "h:mm:ss tt" : "h:mm tt",
            System.Globalization.CultureInfo.CurrentCulture);
        if (settings is not null)
            ScheduleClockTick(settings);
    }

    private static BitmapImage? TryLoadBitmap(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void SetNonActivatingStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(
            handle,
            -20,
            new nint(style | 0x00000080L | 0x08000000L));
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != 0x0084)
            return 0;
        if (!IsTransparentPixel())
            return 0;
        handled = true;
        return new nint(-1);
    }

    private bool IsTransparentPixel()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetCursorPos(out var cursor) ||
            !GetWindowRect(handle, out var window))
        {
            return false;
        }
        var scale = DesktopGeometry.GetWindowDpiScale(handle);
        var margin = OrbChromePadding * scale;
        var orbLeft = window.Left + margin;
        var orbTop = window.Top + margin;
        var orbSize = _sizeDip * scale;
        var x = cursor.X - orbLeft;
        var y = cursor.Y - orbTop;
        if (x < 0 || y < 0 || x >= orbSize || y >= orbSize)
            return false;
        if (_imageShape == OrbImageShape.Circle)
        {
            var dx = x - orbSize / 2;
            var dy = y - orbSize / 2;
            return dx * dx + dy * dy > Math.Pow(orbSize / 2, 2);
        }
        if (_imageBgra is null ||
            _imageStride <= 0 ||
            _imagePixelWidth <= 0 ||
            _imagePixelHeight <= 0)
        {
            return false;
        }
        var imageScale = Math.Max(
            orbSize / _imagePixelWidth,
            orbSize / _imagePixelHeight);
        var displayWidth = _imagePixelWidth * imageScale;
        var displayHeight = _imagePixelHeight * imageScale;
        var offsetX = (displayWidth - orbSize) / 2;
        var offsetY = (displayHeight - orbSize) / 2;
        var imageX = (int)Math.Floor((x + offsetX) / imageScale);
        var imageY = (int)Math.Floor((y + offsetY) / imageScale);
        if (imageX < 0 ||
            imageY < 0 ||
            imageX >= _imagePixelWidth ||
            imageY >= _imagePixelHeight)
        {
            return true;
        }
        return _imageBgra[imageY * _imageStride + imageX * 4 + 3] < 16;
    }

    private void CaptureAlpha(BitmapSource? bitmap)
    {
        _imageBgra = null;
        if (bitmap is null)
            return;
        try
        {
            BitmapSource source = bitmap.Format == PixelFormats.Bgra32
                ? bitmap
                : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            _imageStride = checked(source.PixelWidth * 4);
            _imageBgra = new byte[checked(_imageStride * source.PixelHeight)];
            source.CopyPixels(_imageBgra, _imageStride, 0);
            _imagePixelWidth = source.PixelWidth;
            _imagePixelHeight = source.PixelHeight;
        }
        catch
        {
            _imageBgra = null;
        }
    }

    private void ScheduleClockTick(AppSettings settings)
    {
        _clockTimer.Stop();
        if (!settings.Orb.ShowClock)
            return;
        if (settings.Orb.ShowSeconds)
        {
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
        }
        else
        {
            var now = DateTime.Now;
            var secondsToNextMinute = 60 - now.Second + now.Millisecond / 1000.0;
            _clockTimer.Interval = TimeSpan.FromSeconds(
                Math.Max(1, secondsToNextMinute));
        }
        _clockTimer.Start();
    }

    private static void AddMenu(
        System.Windows.Controls.ContextMenu menu,
        string text,
        Action action)
    {
        var item = new System.Windows.Controls.MenuItem { Header = text };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    private PixelRect GetPhysicalBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return GetWindowRect(handle, out var rect)
            ? new PixelRect(
                rect.Left,
                rect.Top,
                Math.Max(1, rect.Right - rect.Left),
                Math.Max(1, rect.Bottom - rect.Top))
            : new PixelRect(
                (int)Math.Round(Left),
                (int)Math.Round(Top),
                Math.Max(1, (int)Math.Round(ActualWidth)),
                Math.Max(1, (int)Math.Round(ActualHeight)));
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
