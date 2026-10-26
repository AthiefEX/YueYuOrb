using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class TranslationOverlayWindow : Window
{
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int MonitorDefaultToNearest = 2;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const double AnchorGap = 14;
    private const double WorkAreaMargin = 8;

    private readonly Action<string> _clipboardWriter;
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _isSubtitleMode;
    private bool _subtitlePaused;
    private bool _canReselect;
    private bool _isDragging;
    private Point _dragPointerOffset;

    public TranslationOverlayWindow()
    {
        InitializeComponent();
        _clipboardWriter = text => Clipboard.SetText(text);
        ShowActivated = false;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
        ApplySettings(_settings);
    }

    public event Action? PauseResumeRequested;
    public event Action? ReselectRegionRequested;
    public event Action? StopSubtitleRequested;
    public event Action? CopyRequested;

    public bool IsSubtitleMode => _isSubtitleMode;
    public bool IsCaptureExcluded { get; private set; }
    public string TranslatedText => TranslatedTextBlock.Text;
    public string StatusText => StatusTextBlock.Text;

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RunOnUiThread(() =>
        {
            _settings = settings;
            Topmost = settings.Overlay.AlwaysOnTop;
            Opacity = Math.Clamp(
                settings.Translation.SubtitleOverlayOpacity,
                0.6,
                1.0);
            var fontSize = Math.Clamp(
                settings.Translation.SubtitleFontSizeDip,
                14,
                36);
            TranslatedTextBlock.FontSize = fontSize;
            OriginalTextBlock.FontSize = Math.Max(12, fontSize - 5);
            StatusTextBlock.FontSize = Math.Clamp(fontSize * 0.62, 11, 16);

            if (Application.Current is { } application)
            {
                ThemeService.ApplyToResources(
                    application.Resources,
                    settings.General.Theme,
                    SystemParameters.HighContrast,
                    ThemeService.SystemUsesDarkTheme());
            }

            if (_isSubtitleMode && IsVisible)
                PositionWindow(subtitleMode: true, default);
        });
    }

    public void ShowStatus(string status, PixelPoint anchor) =>
        RunOnUiThread(() =>
        {
            if (!_isSubtitleMode)
                ClearTranslationContent();
            StatusTextBlock.Text = status ?? "";
            ShowAt(anchor, subtitleMode: _isSubtitleMode);
        });

    public void ShowResult(TranslationResponse response, PixelPoint anchor)
    {
        ArgumentNullException.ThrowIfNull(response);
        RunOnUiThread(() =>
        {
            OriginalTextBlock.Text = response.OriginalText ?? "";
            OriginalPanel.Visibility =
                string.IsNullOrWhiteSpace(response.OriginalText)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            MetadataTextBlock.Text = BuildMetadata(response);

            if (response.Success &&
                !string.IsNullOrWhiteSpace(response.TranslatedText))
            {
                TranslatedTextBlock.Text = response.TranslatedText;
                TranslatedPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "翻译完成";
                CopyButton.IsEnabled = true;
            }
            else
            {
                TranslatedTextBlock.Text = "";
                TranslatedPanel.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = ResolveFailureMessage(response);
                CopyButton.IsEnabled = false;
            }

            ShowAt(anchor, subtitleMode: _isSubtitleMode);
        });
    }

    public void ShowError(string message, PixelPoint anchor) =>
        RunOnUiThread(() =>
        {
            TranslatedTextBlock.Text = "";
            TranslatedPanel.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = false;
            StatusTextBlock.Text = message ?? "翻译失败。";
            ShowAt(anchor, subtitleMode: _isSubtitleMode);
        });

    public void SetSubtitleControls(
        bool active,
        bool paused,
        bool canReselect) =>
        RunOnUiThread(() =>
        {
            _isSubtitleMode = active;
            _subtitlePaused = paused;
            _canReselect = canReselect;
            SubtitleControlBar.Visibility = active
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!active)
            {
                HideAndClear();
                return;
            }

            PauseResumeButton.Content = paused ? "继续" : "暂停";
            AutomationProperties.SetName(
                PauseResumeButton,
                paused ? "继续字幕翻译" : "暂停字幕翻译");
            ReselectButton.IsEnabled = canReselect;
            if (string.IsNullOrWhiteSpace(StatusTextBlock.Text))
                StatusTextBlock.Text = paused
                    ? "字幕翻译已暂停。"
                    : "字幕翻译准备中。";
            PrepareInitialReveal();
            if (!IsVisible)
                ShowWindowSafely();
            UpdateLayout();
            PositionWindow(subtitleMode: true, default);
            BeginContentTransition(initialReveal: true);
        });

    private void ShowAt(PixelPoint anchor, bool subtitleMode)
    {
        PrepareInitialReveal();
        if (!ShowWindowSafely())
            return;

        UpdateLayout();
        PositionWindow(subtitleMode, anchor);
        BeginContentTransition(initialReveal: true);
    }

    private bool ShowWindowSafely()
    {
        if (IsVisible)
            return true;
        try
        {
            Show();
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ObjectDisposedException or
            ExternalException)
        {
            return false;
        }
    }

    private void PrepareInitialReveal()
    {
        TransitionLayer.BeginAnimation(OpacityProperty, null);
        TransitionTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        TransitionLayer.Opacity = AnimationsEnabled ? 0 : 1;
        TransitionTranslate.Y = AnimationsEnabled ? 7 : 0;
    }

    private void BeginContentTransition(bool initialReveal)
    {
        if (!AnimationsEnabled)
        {
            TransitionLayer.BeginAnimation(OpacityProperty, null);
            TransitionTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            TransitionLayer.Opacity = 1;
            TransitionTranslate.Y = 0;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(120);
        var easing = new CubicEase
        {
            EasingMode = EasingMode.EaseOut
        };
        var opacity = new DoubleAnimation
        {
            From = initialReveal ? TransitionLayer.Opacity : 0.55,
            To = 1,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        var translate = new DoubleAnimation
        {
            From = initialReveal ? TransitionTranslate.Y : 3,
            To = 0,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        opacity.Completed += (_, _) =>
        {
            TransitionLayer.BeginAnimation(OpacityProperty, null);
            TransitionTranslate.BeginAnimation(
                TranslateTransform.YProperty,
                null);
            TransitionLayer.Opacity = 1;
            TransitionTranslate.Y = 0;
        };
        TransitionLayer.BeginAnimation(
            OpacityProperty,
            opacity,
            HandoffBehavior.SnapshotAndReplace);
        TransitionTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            translate,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static bool AnimationsEnabled =>
        SystemParameters.ClientAreaAnimation &&
        !SystemParameters.HighContrast;

    private void PositionWindow(bool subtitleMode, PixelPoint anchor)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0 || !TryGetWindowBounds(handle, out var bounds))
                return;

            var monitor = subtitleMode
                ? ResolveSubtitleMonitor()
                : ResolveAnchorMonitor(anchor);
            if (monitor is null)
                return;

            var target = subtitleMode
                ? GetSubtitlePosition(bounds, monitor.WorkArea)
                : GetAnchorPosition(bounds, monitor.WorkArea, anchor);
            MoveWindow(handle, target);

            if (TryGetWindowBounds(handle, out var moved) &&
                (moved.Width != bounds.Width ||
                 moved.Height != bounds.Height))
            {
                target = subtitleMode
                    ? GetSubtitlePosition(moved, monitor.WorkArea)
                    : GetAnchorPosition(
                        moved,
                        monitor.WorkArea,
                        anchor);
                MoveWindow(handle, target);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ObjectDisposedException or
            ExternalException or
            ArgumentException or
            OverflowException)
        {
            // A disappearing monitor or disposed HWND must not disrupt output.
        }
    }

    private static PixelPoint GetAnchorPosition(
        PixelRect window,
        PixelRect workArea,
        PixelPoint anchor)
    {
        var left = ClampAdd(anchor.X, (int)AnchorGap);
        var top = ClampAdd(anchor.Y, (int)AnchorGap);
        return ClampToWorkArea(
            new PixelPoint(left, top),
            window.Size,
            workArea);
    }

    private PixelPoint GetSubtitlePosition(
        PixelRect window,
        PixelRect workArea)
    {
        if (_settings.Translation.SubtitleOverlayPlacement is { } saved)
        {
            var availableWidth = Math.Max(0, workArea.Width - window.Width);
            var availableHeight = Math.Max(0, workArea.Height - window.Height);
            return ClampToWorkArea(
                new PixelPoint(
                    ClampAdd(
                        workArea.Left,
                        (int)Math.Round(saved.RelativeX * availableWidth)),
                    ClampAdd(
                        workArea.Top,
                        (int)Math.Round(saved.RelativeY * availableHeight))),
                window.Size,
                workArea);
        }

        return ClampToWorkArea(
            new PixelPoint(
                workArea.Left +
                    Math.Max(0, (workArea.Width - window.Width) / 2),
                workArea.Bottom - window.Height -
                    Math.Max((int)WorkAreaMargin, 12)),
            window.Size,
            workArea);
    }

    private DisplayMonitor? ResolveSubtitleMonitor()
    {
        try
        {
            var monitors = DisplayTopology.GetMonitors();
            var monitorId =
                _settings.Translation.SubtitleOverlayPlacement?.MonitorId;
            return monitors.FirstOrDefault(item =>
                       !string.IsNullOrWhiteSpace(monitorId) &&
                       string.Equals(
                           item.DeviceName,
                           monitorId,
                           StringComparison.OrdinalIgnoreCase))
                   ?? DisplayTopology.SelectPrimaryMonitor(monitors);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ExternalException)
        {
            return null;
        }
    }

    private static DisplayMonitor? ResolveAnchorMonitor(PixelPoint anchor)
    {
        try
        {
            return DisplayTopology.FromPoint(anchor)
                ?? DisplayTopology.SelectPrimaryMonitor(
                    DisplayTopology.GetMonitors());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ExternalException)
        {
            var width = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
            var height = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
            var bounds = new PixelRect(0, 0, width, height);
            return new DisplayMonitor(
                "primary",
                bounds,
                bounds,
                IsPrimary: true);
        }
    }

    private static PixelPoint ClampToWorkArea(
        PixelPoint desired,
        PixelSize size,
        PixelRect workArea)
    {
        var width = Math.Min(size.Width, workArea.Width);
        var height = Math.Min(size.Height, workArea.Height);
        var minimumLeft = workArea.Left;
        var minimumTop = workArea.Top;
        var maximumLeft = workArea.Right - width;
        var maximumTop = workArea.Bottom - height;
        if (maximumLeft < minimumLeft)
            minimumLeft = maximumLeft = workArea.Left;
        if (maximumTop < minimumTop)
            minimumTop = maximumTop = workArea.Top;

        return new PixelPoint(
            Math.Clamp(desired.X, minimumLeft, maximumLeft),
            Math.Clamp(desired.Y, minimumTop, maximumTop));
    }

    private void MoveWindow(nint handle, PixelPoint point)
    {
        var insertAfter = Topmost
            ? new nint(-1)
            : new nint(-2);
        _ = SetWindowPos(
            handle,
            insertAfter,
            point.X,
            point.Y,
            0,
            0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static bool TryGetWindowBounds(
        nint handle,
        out PixelRect bounds)
    {
        if (handle != 0 && GetWindowRect(handle, out var native))
        {
            bounds = new PixelRect(
                native.Left,
                native.Top,
                Math.Max(1, native.Right - native.Left),
                Math.Max(1, native.Bottom - native.Top));
            return true;
        }

        bounds = default;
        return false;
    }

    private static int ClampAdd(int value, int amount)
    {
        var result = (long)value + amount;
        return (int)Math.Clamp(
            result,
            int.MinValue,
            int.MaxValue);
    }

    private static string BuildMetadata(TranslationResponse response)
    {
        var language = string.IsNullOrWhiteSpace(response.SourceLanguage)
            ? "自动检测"
            : response.SourceLanguage;
        var provider = string.IsNullOrWhiteSpace(response.ProviderName)
            ? "翻译服务"
            : response.ProviderName;
        return $"{language} · {provider}";
    }

    private static string ResolveFailureMessage(
        TranslationResponse response) =>
        !string.IsNullOrWhiteSpace(response.Message)
            ? response.Message
            : response.FailureKind switch
            {
                TranslationFailureKind.EmptyText =>
                    "未检测到可翻译的文字。",
                TranslationFailureKind.NoTranslationNeeded =>
                    "内容已经是目标语言。",
                TranslationFailureKind.UnsupportedLanguage =>
                    "当前语言组合不受支持。",
                TranslationFailureKind.Authentication =>
                    "翻译服务认证失败，请检查 API 密钥。",
                TranslationFailureKind.Quota =>
                    "翻译服务额度已用尽，请稍后重试或更换服务。",
                TranslationFailureKind.Timeout =>
                    "翻译请求超时，请重试。",
                TranslationFailureKind.Network =>
                    "网络连接不可用，请检查网络后重试。",
                TranslationFailureKind.Cancelled =>
                    "翻译已取消。",
                _ => "翻译失败，请稍后重试。"
            };

    private void ClearTranslationContent()
    {
        OriginalTextBlock.Text = "";
        TranslatedTextBlock.Text = "";
        TranslatedPanel.Visibility = Visibility.Collapsed;
        MetadataTextBlock.Text = "";
        OriginalPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
    }

    private void HideAndClear()
    {
        ClearTranslationContent();
        StatusTextBlock.Text = "";
        TransitionLayer.BeginAnimation(OpacityProperty, null);
        TransitionTranslate.BeginAnimation(
            TranslateTransform.YProperty,
            null);
        TransitionLayer.Opacity = 1;
        TransitionTranslate.Y = 0;
        try
        {
            Hide();
        }
        catch (InvalidOperationException)
        {
            // Closing can dispose the HWND while asynchronous work completes.
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TranslatedTextBlock.Text))
            return;
        try
        {
            _clipboardWriter(TranslatedTextBlock.Text);
        }
        catch (Exception exception) when (
            exception is COMException or
            ExternalException or
            InvalidOperationException)
        {
            StatusTextBlock.Text = "无法访问剪贴板，请稍后重试。";
            return;
        }

        CopyRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        HideAndClear();

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        if (_isSubtitleMode)
            PauseResumeRequested?.Invoke();
    }

    private void OnReselectClick(object sender, RoutedEventArgs e)
    {
        if (_isSubtitleMode && _canReselect)
            ReselectRegionRequested?.Invoke();
    }

    private void OnStopSubtitleClick(object sender, RoutedEventArgs e)
    {
        if (_isSubtitleMode)
            StopSubtitleRequested?.Invoke();
    }

    private void OnTitleBarMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isSubtitleMode ||
            e.ChangedButton != MouseButton.Left ||
            !TryGetWindowBounds(
                new WindowInteropHelper(this).Handle,
                out var window))
        {
            return;
        }

        var pointer = PointToScreen(e.GetPosition(this));
        _dragPointerOffset = new Point(
            pointer.X - window.Left,
            pointer.Y - window.Top);
        _isDragging = true;
        TitleBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnTitleBarMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pointer = PointToScreen(e.GetPosition(this));
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 ||
            !TryGetWindowBounds(handle, out var current))
        {
            return;
        }

        var monitor = ResolveAnchorMonitor(
            new PixelPoint(
                (int)Math.Round(pointer.X),
                (int)Math.Round(pointer.Y)));
        if (monitor is null)
            return;
        var target = ClampToWorkArea(
            new PixelPoint(
                (int)Math.Round(pointer.X - _dragPointerOffset.X),
                (int)Math.Round(pointer.Y - _dragPointerOffset.Y)),
            current.Size,
            monitor.WorkArea);
        MoveWindow(handle, target);
        e.Handled = true;
    }

    private void OnTitleBarMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;
        _isDragging = false;
        TitleBar.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;

        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        _ = SetWindowLongPtr(
            handle,
            GWL_EXSTYLE,
            new nint(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        IsCaptureExcluded = WindowCaptureExclusion.TryExclude(handle);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _isDragging = false;
        ClearTranslationContent();
        StatusTextBlock.Text = "";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ClearTranslationContent();
        StatusTextBlock.Text = "";
    }

    private bool RunOnUiThread(Action action)
    {
        if (Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return false;
        }
        if (Dispatcher.CheckAccess())
        {
            action();
            return true;
        }

        try
        {
            Dispatcher.Invoke(action, DispatcherPriority.Send);
            return true;
        }
        catch (Exception exception) when (
            exception is TaskCanceledException or
            InvalidOperationException or
            ObjectDisposedException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(
        nint hwnd,
        int index,
        nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint hwnd,
        out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }
}
