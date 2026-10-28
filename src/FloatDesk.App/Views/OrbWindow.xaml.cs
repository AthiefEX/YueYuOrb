using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
    private readonly Action<bool>? _panelToggle;
    private readonly Func<bool>? _panelOpenState;
    private readonly Action<PixelRect> _positionChanged;
    private readonly Action _toggleVisibility;
    private readonly Action _exit;
    private readonly string _orbImagePath;
    private readonly Effect? _classicOrbEffect;
    private readonly OrbDragMotion _dragMotion = new();
    private AppSettings? _settings;
    private PixelRect? _pendingDragBounds;
    private bool _dragFrameSubscribed;
    private double _sizeDip = 56;
    private bool _snap = true;
    private bool _positionLocked;
    private bool _panelOpenAtPress;
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
        string? orbImagePath = null,
        Action<bool>? panelToggle = null,
        Func<bool>? panelOpenState = null)
    {
        InitializeComponent();
        _classicOrbEffect = OrbCircle.Effect;
        _openPanel = openPanel;
        _panelToggle = panelToggle;
        _panelOpenState = panelOpenState;
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
        var crystal = settings.Orb.DisplayMode == OrbDisplayMode.Crystal;
        _imageShape = crystal ? OrbImageShape.Circle : settings.Orb.ImageShape;
        OrbLayout.Margin = new Thickness(OrbChromePadding);
        Width = _sizeDip + (OrbChromePadding * 2);
        OrbRoot.Width = _sizeDip;
        OrbRoot.Height = _sizeDip;
        Opacity = settings.Orb.Opacity;
        var imageInset = crystal ? _sizeDip * 0.035 : 0;
        var imageDiameter = _sizeDip - imageInset * 2;
        OrbImage.Margin = new Thickness(imageInset);
        OrbCircle.Margin = new Thickness(imageInset);
        OrbImage.UseLayoutRounding = !crystal;
        OrbCircle.UseLayoutRounding = !crystal;
        OrbCircle.Effect = crystal ? null : _classicOrbEffect;
        OrbRoot.Background = crystal ? Brushes.Transparent : null;
        CrystalSurface.Visibility = crystal ? Visibility.Visible : Visibility.Collapsed;
        OrbRoot.Clip = crystal
            ? new EllipseGeometry(new Point(_sizeDip / 2, _sizeDip / 2), _sizeDip / 2, _sizeDip / 2)
            : null;
        OrbImage.Clip = _imageShape == OrbImageShape.Circle
            ? new System.Windows.Media.EllipseGeometry(
                new System.Windows.Point(imageDiameter / 2, imageDiameter / 2),
                imageDiameter / 2,
                imageDiameter / 2)
            : null;
        var showClock = settings.Orb.ShowClock;
        ClockBorder.Visibility = showClock
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (showClock)
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
        FallbackText.Visibility = bitmap is null && !showClock
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        _panelOpenAtPress = _panelOpenState?.Invoke() ?? false;
        if (GetCursorPos(out var cursor))
        {
            var handle = new WindowInteropHelper(this).Handle;
            var threshold = Math.Max(
                1,
                (int)Math.Round(
                    3 * DesktopGeometry.GetWindowDpiScale(handle)));
            _dragMotion.Begin(
                new PixelPoint(cursor.X, cursor.Y),
                GetPhysicalBounds(),
                threshold,
                canDrag: !_positionLocked);
        }
        else
        {
            _dragMotion.Reset();
        }
        OrbRoot.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!OrbRoot.IsMouseCaptured || _positionLocked)
            return;
        if (!GetCursorPos(out var cursor))
            return;
        var pointer = new PixelPoint(cursor.X, cursor.Y);
        if (!_dragMotion.TryUpdate(pointer, out var target))
        {
            return;
        }

        target = ConstrainToMonitor(target, pointer);
        if (!_dragFrameSubscribed)
            BeginDragVisuals();
        QueueDragPosition(target);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!OrbRoot.IsMouseCaptured)
            return;
        PixelPoint? releasePointer = null;
        if (GetCursorPos(out var cursor))
        {
            releasePointer = new PixelPoint(cursor.X, cursor.Y);
            if (_dragMotion.TryUpdate(releasePointer.Value, out var finalTarget))
                _pendingDragBounds = ConstrainToMonitor(
                    finalTarget,
                    releasePointer.Value);
        }
        FlushPendingDragMove();
        var wasDragging = _dragMotion.IsDragging;
        OrbRoot.ReleaseMouseCapture();
        EndDragVisuals();
        _dragMotion.Reset();
        _pendingDragBounds = null;
        if (wasDragging)
        {
            if (releasePointer is { } pointer)
            {
                var bounds = GetPhysicalBounds();
                var constrained = ConstrainToMonitor(bounds, pointer);
                if (constrained != bounds)
                    SetPosition(constrained);
            }
            _positionChanged(GetPhysicalBounds());
        }
        else
        {
            if (_panelToggle is null)
                _openPanel();
            else
                _panelToggle(_panelOpenAtPress);
        }
        e.Handled = true;
    }

    private void QueueDragPosition(PixelRect target)
    {
        _pendingDragBounds = target;
        if (_dragFrameSubscribed)
            return;
        CompositionTarget.Rendering += OnDragRenderFrame;
        _dragFrameSubscribed = true;
    }

    private void OnDragRenderFrame(object? sender, EventArgs e) =>
        FlushPendingDragMove();

    private void FlushPendingDragMove()
    {
        if (_pendingDragBounds is not { } target)
            return;
        _pendingDragBounds = null;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;
        SetWindowPos(
            handle,
            0,
            target.Left,
            target.Top,
            0,
            0,
            0x0001 | 0x0004 | 0x0010);
    }

    private void BeginDragVisuals()
    {
        OrbDragScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            ScaleAnimation(1.045, 90));
        OrbDragScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            ScaleAnimation(1.045, 90));
    }

    private void EndDragVisuals()
    {
        _pendingDragBounds = null;
        if (_dragFrameSubscribed)
        {
            CompositionTarget.Rendering -= OnDragRenderFrame;
            _dragFrameSubscribed = false;
        }
        OrbDragScale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            ScaleAnimation(1, 150));
        OrbDragScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            ScaleAnimation(1, 150));
    }

    private static DoubleAnimation ScaleAnimation(
        double to,
        int durationMilliseconds) =>
        new()
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

    private PixelRect ConstrainToMonitor(PixelRect bounds, PixelPoint pointer)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = DisplayTopology.FromPoint(pointer)
            ?? DisplayTopology.FromWindow(handle);
        if (monitor is null)
            return bounds;
        var scale = DesktopGeometry.GetWindowDpiScale(
            handle);
        var threshold = _snap
            ? Math.Max(1, (int)Math.Round(16 * scale))
            : 0;
        return OrbPlacement.SnapToWorkArea(
            bounds,
            monitor.WorkArea,
            threshold);
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
            image.CreateOptions =
                BitmapCreateOptions.PreservePixelFormat |
                BitmapCreateOptions.IgnoreImageCache;
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
        return IsTransparentOrbPoint(new Point(
            (cursor.X - orbLeft) / scale,
            (cursor.Y - orbTop) / scale));
    }

    internal bool IsTransparentOrbPoint(Point point)
    {
        var orbSize = _sizeDip;
        var x = point.X;
        var y = point.Y;
        if (x < 0 || y < 0 || x >= orbSize || y >= orbSize)
            return true;
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
