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
using FloatDesk.App.Translation;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

/// <summary>
/// Identifies one overlay operation. Callers must retain the token returned by
/// <see cref="TranslationOverlayWindow.BeginOperation"/> and pass it to every
/// status, result, error, and subtitle update belonging to that operation.
/// </summary>
public readonly struct TranslationOverlayOperationToken
{
    internal TranslationOverlayOperationToken(long generation)
    {
        Generation = generation;
    }

    internal long Generation { get; }
}

public partial class TranslationOverlayWindow : Window
{
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int MonitorDefaultToNearest = 2;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const double AnchorGap = 14;
    private const double WorkAreaMargin = 8;
    private const double DefaultOverlayWidth = 480;
    private const double MinimumInteractiveWidth = 340;
    private const double MaximumSubtitleWidth = 720;

    private readonly Action<string> _clipboardWriter;
    private AppSettings _settings = AppSettings.CreateDefault();
    private bool _isSubtitleMode;
    private bool _isClickThrough;
    private bool _subtitlePaused;
    private bool _canReselect;
    private bool _isDragging;
    private bool _dismissed;
    private bool _isClosed;
    private long _activeOperationGeneration;
    private Point _dragPointerOffset;
    private PixelRect? _subtitleCaptureRegion;
    private string? _subtitleRegionMonitorId;

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
    public event Action? InteractiveOperationDismissed;

    public bool IsSubtitleMode => _isSubtitleMode;
    internal bool IsClickThrough => _isClickThrough;
    public bool IsDismissed => _dismissed;
    public bool IsCaptureExcluded { get; private set; }
    internal bool CanRestoreAfterCapture =>
        !_isClosed &&
        !_dismissed &&
        _activeOperationGeneration != 0;
    internal bool HasActiveOperation
    {
        get
        {
            var active = false;
            RunOnUiThread(() =>
                active =
                    !_isClosed &&
                    !_dismissed &&
                    _activeOperationGeneration != 0);
            return active;
        }
    }
    public string TranslatedText =>
        _isSubtitleMode
            ? SubtitleTranslationTextBlock.Text
            : TranslatedTextBlock.Text;
    public string StatusText =>
        _isSubtitleMode
            ? SubtitleNoticeTextBlock.Text
            : StatusTextBlock.Text;

    /// <summary>
    /// Starts a clean operation after an earlier result was dismissed.
    /// Interactive operations start hidden and one-shot; subtitle operations
    /// start as a compact notice surface. The returned token must be carried
    /// through all updates for that operation.
    /// </summary>
    public TranslationOverlayOperationToken BeginOperation(
        bool subtitle = false)
    {
        var operation = default(TranslationOverlayOperationToken);
        RunOnUiThread(() =>
        {
            if (_isClosed)
                return;

            _dismissed = false;
            var generation = NextGeneration(_activeOperationGeneration);
            _activeOperationGeneration = generation;
            operation = new TranslationOverlayOperationToken(generation);
            ResetOperationPresentation();
            if (subtitle)
                BeginSubtitlePresentation(operation);
        });
        return operation;
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RunOnUiThread(() =>
        {
            _settings = settings;
            _subtitleRegionMonitorId =
                settings.Translation.SubtitleRegion?.MonitorId;
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
            SubtitleTranslationTextBlock.FontSize = fontSize;
            SubtitleNoticeTextBlock.FontSize = Math.Clamp(
                fontSize * 0.62,
                11,
                16);
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
            {
                UpdateLayout();
                PositionWindow(subtitleMode: true, default);
            }
        });
    }

    public void ShowStatus(
        TranslationOverlayOperationToken operation,
        string status,
        PixelPoint anchor) =>
        RunOnUiThread(() =>
        {
            if (!IsCurrentOperation(operation))
                return;

            if (_isSubtitleMode)
            {
                ShowSubtitleNoticeCore(operation, status);
                return;
            }

            if (!_isSubtitleMode)
                ClearTranslationContent();
            StatusTextBlock.Text = status ?? "";
            ShowAt(operation, anchor, subtitleMode: _isSubtitleMode);
        });

    public void ShowResult(
        TranslationOverlayOperationToken operation,
        TranslationResponse response,
        PixelPoint anchor)
    {
        ArgumentNullException.ThrowIfNull(response);
        RunOnUiThread(() =>
        {
            if (!IsCurrentOperation(operation))
                return;

            if (_isSubtitleMode)
            {
                if (response.Success &&
                    !string.IsNullOrWhiteSpace(response.TranslatedText))
                {
                    ShowSubtitleTextCore(
                        operation,
                        response.TranslatedText);
                }
                else
                {
                    ShowSubtitleNoticeCore(
                        operation,
                        ResolveFailureMessage(response));
                }
                return;
            }

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

            ShowAt(operation, anchor, subtitleMode: _isSubtitleMode);
        });
    }

    public void ShowError(
        TranslationOverlayOperationToken operation,
        string message,
        PixelPoint anchor) =>
        RunOnUiThread(() =>
        {
            if (!IsCurrentOperation(operation))
                return;

            if (_isSubtitleMode)
            {
                ShowSubtitleNoticeCore(
                    operation,
                    message ?? "翻译失败。");
                return;
            }

            if (!_isSubtitleMode)
                ClearTranslationContent();
            TranslatedTextBlock.Text = "";
            TranslatedPanel.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = false;
            StatusTextBlock.Text = message ?? "翻译失败。";
            ShowAt(operation, anchor, subtitleMode: _isSubtitleMode);
        });

    public TranslationSubtitlePlacementStatus ShowSubtitleText(
        TranslationOverlayOperationToken operation,
        string translatedText)
    {
        var status = TranslationSubtitlePlacementStatus.NoSafePlacement;
        RunOnUiThread(() =>
            status = ShowSubtitleTextCore(operation, translatedText));
        return status;
    }

    public void ShowSubtitleNotice(
        TranslationOverlayOperationToken operation,
        string notice) =>
        RunOnUiThread(() =>
            ShowSubtitleNoticeCore(operation, notice));

    private TranslationSubtitlePlacementStatus ShowSubtitleTextCore(
        TranslationOverlayOperationToken operation,
        string translatedText)
    {
        if (!IsCurrentOperation(operation) ||
            string.IsNullOrWhiteSpace(translatedText))
        {
            return TranslationSubtitlePlacementStatus.Ready;
        }

        ApplySubtitlePresentation();
        var previousText = SubtitleTranslationTextBlock.Text;
        var previousNotice = SubtitleNoticeTextBlock.Text;
        var previousNoticeVisibility = SubtitleNoticeTextBlock.Visibility;
        var wasVisible = IsVisible;

        SubtitleTranslationTextBlock.Text = "";
        SubtitleTranslationTextBlock.Text = translatedText;
        SubtitleNoticeTextBlock.Text = "";
        SubtitleNoticeTextBlock.Visibility = Visibility.Collapsed;
        SubtitleSurface.Visibility = Visibility.Visible;

        var status = PositionSubtitleSurfaceCore();
        if (status == TranslationSubtitlePlacementStatus.Ready)
            return status;

        SubtitleTranslationTextBlock.Text = previousText;
        SubtitleNoticeTextBlock.Text = previousNotice;
        SubtitleNoticeTextBlock.Visibility = previousNoticeVisibility;
        if (!wasVisible)
            HideWithoutClearingContent();
        return status;
    }

    private void ShowSubtitleNoticeCore(
        TranslationOverlayOperationToken operation,
        string? notice)
    {
        if (!IsCurrentOperation(operation))
            return;

        ApplySubtitlePresentation();
        SubtitleNoticeTextBlock.Text = notice ?? "";
        SubtitleNoticeTextBlock.Visibility =
            string.IsNullOrWhiteSpace(notice)
                ? Visibility.Collapsed
                : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(SubtitleTranslationTextBlock.Text) ||
            SubtitleNoticeTextBlock.Visibility == Visibility.Visible)
        {
            SubtitleSurface.Visibility = Visibility.Visible;
            _ = PositionSubtitleSurfaceCore();
        }
        else
        {
            SubtitleSurface.Visibility = Visibility.Collapsed;
            HideWithoutClearingContent();
        }
    }

    public void SetSubtitleControls(
        TranslationOverlayOperationToken operation,
        bool active,
        bool paused,
        bool canReselect) =>
        RunOnUiThread(() =>
        {
            if (!IsCurrentOperation(operation))
                return;

            if (!active)
            {
                Dismiss();
                return;
            }

            var enteringSubtitleMode = active && !_isSubtitleMode;
            if (enteringSubtitleMode)
            {
                ApplySubtitlePresentation();
                ClearTranslationContent();
            }

            _isSubtitleMode = active;
            _subtitlePaused = paused;
            _canReselect = canReselect;
            SubtitleControlBar.Visibility = Visibility.Collapsed;

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
            if (string.IsNullOrWhiteSpace(SubtitleNoticeTextBlock.Text) &&
                string.IsNullOrWhiteSpace(SubtitleTranslationTextBlock.Text))
            {
                SubtitleNoticeTextBlock.Text = paused
                    ? "字幕翻译已暂停。"
                    : "字幕翻译准备中。";
                SubtitleNoticeTextBlock.Visibility = Visibility.Visible;
            }
            SubtitleSurface.Visibility =
                string.IsNullOrWhiteSpace(SubtitleTranslationTextBlock.Text) &&
                string.IsNullOrWhiteSpace(SubtitleNoticeTextBlock.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            _ = PositionSubtitleSurfaceCore();
        });

    internal TranslationSubtitlePlacementStatus PrepareSubtitleCaptureRegion(
        TranslationCaptureRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        var status = TranslationSubtitlePlacementStatus.NoSafePlacement;
        RunOnUiThread(() =>
            status = PrepareSubtitleCaptureRegionCore(region));
        return status;
    }

    internal bool IsOperationActive(
        TranslationOverlayOperationToken operation)
    {
        var active = false;
        RunOnUiThread(() => active = IsCurrentOperation(operation));
        return active;
    }

    internal void DismissCurrentOperation() =>
        RunOnUiThread(Dismiss);

    internal void DismissOperation(
        TranslationOverlayOperationToken operation) =>
        RunOnUiThread(() =>
        {
            if (IsCurrentOperation(operation))
                Dismiss();
        });

    private TranslationSubtitlePlacementStatus PrepareSubtitleCaptureRegionCore(
        TranslationCaptureRegion region)
    {
        _subtitleRegionMonitorId = region.MonitorId;
        DisplayMonitor? monitor;
        try
        {
            monitor = DisplayTopology.GetMonitors().FirstOrDefault(item =>
                string.Equals(
                    item.DeviceName,
                    region.MonitorId,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ExternalException)
        {
            monitor = null;
        }

        if (monitor is null)
        {
            _subtitleCaptureRegion = null;
            return TranslationSubtitlePlacementStatus.MonitorMissing;
        }

        _subtitleCaptureRegion = ToPixelBounds(monitor, region);
        if (!_isSubtitleMode || _isClosed)
            return TranslationSubtitlePlacementStatus.Ready;

        return IsVisible
            ? PositionSubtitleSurfaceCore()
            : TranslationSubtitlePlacementStatus.Ready;
    }

    private void ApplySubtitlePresentation()
    {
        _isSubtitleMode = true;
        ApplyClickThrough(clickThrough: true);
        CardBackdrop.Visibility = Visibility.Collapsed;
        TransitionLayer.Visibility = Visibility.Collapsed;
        SubtitleSurface.Visibility = Visibility.Visible;
        SubtitleControlBar.Visibility = Visibility.Collapsed;
        OverlayCard.Background = Brushes.Transparent;
        OverlayCard.BorderThickness = new Thickness(0);
        OverlayCard.Padding = new Thickness(0);
        OverlayCard.CornerRadius = new CornerRadius(0);
        MinWidth = 0;
        MaxWidth = MaximumSubtitleWidth;
    }

    private void ApplyFullCardPresentation()
    {
        ApplyClickThrough(clickThrough: false);
        CardBackdrop.Visibility = Visibility.Visible;
        TransitionLayer.Visibility = Visibility.Visible;
        SubtitleSurface.Visibility = Visibility.Collapsed;
        SubtitleControlBar.Visibility = Visibility.Collapsed;
        OverlayCard.SetResourceReference(
            Border.BackgroundProperty,
            "SettingsCardBrush");
        OverlayCard.SetResourceReference(
            Border.BorderBrushProperty,
            "SettingsAccentOutlineBrush");
        OverlayCard.BorderThickness = new Thickness(1);
        OverlayCard.Padding = new Thickness(1);
        OverlayCard.CornerRadius = new CornerRadius(22);
        MinWidth = MinimumInteractiveWidth;
        MaxWidth = MaximumSubtitleWidth;
        Width = DefaultOverlayWidth;
    }

    private TranslationSubtitlePlacementStatus PositionSubtitleSurfaceCore()
    {
        var monitor = ResolveSubtitleMonitor();
        if (monitor is null)
            return TranslationSubtitlePlacementStatus.MonitorMissing;

        ConstrainSubtitleWidth(_subtitleCaptureRegion, monitor.WorkArea);
        if (!ShowWindowSafely())
            return TranslationSubtitlePlacementStatus.NoSafePlacement;

        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 ||
            !TryGetWindowBounds(handle, out var bounds))
        {
            return TranslationSubtitlePlacementStatus.NoSafePlacement;
        }

        if (!TryGetSubtitleTarget(bounds, monitor.WorkArea, out var target))
            return TranslationSubtitlePlacementStatus.NoSafePlacement;

        MoveWindow(handle, target);
        if (TryGetWindowBounds(handle, out var moved) &&
            (moved.Width != bounds.Width ||
             moved.Height != bounds.Height) &&
            TryGetSubtitleTarget(moved, monitor.WorkArea, out var adjusted))
        {
            MoveWindow(handle, adjusted);
        }

        return TranslationSubtitlePlacementStatus.Ready;
    }

    private bool TryGetSubtitleTarget(
        PixelRect window,
        PixelRect workArea,
        out PixelPoint target)
    {
        if (_subtitleCaptureRegion is not { } captureRegion)
        {
            target = GetSubtitlePosition(window, workArea);
            return true;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var gap = Math.Max(
            0,
            (int)Math.Round(8 * dpi.DpiScaleY));
        var placement = SubtitleOverlayPlacementPolicy.PlaceAnchored(
            captureRegion,
            window.Size,
            workArea,
            _settings.Translation.SubtitleAttachment,
            gap,
            allowCaptureOverlap: IsCaptureExcluded);
        if (placement is null)
        {
            target = default;
            return false;
        }

        target = placement.Value.Location;
        return true;
    }

    private void ConstrainSubtitleWidth(
        PixelRect? captureRegion,
        PixelRect workArea)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var physicalWidth = captureRegion is { } capture
            ? Math.Min(capture.Width, workArea.Width)
            : workArea.Width;
        var widthDip =
            physicalWidth / Math.Max(0.01, dpi.DpiScaleX);
        Width = Math.Clamp(widthDip, 1, MaximumSubtitleWidth);
        SubtitleSurface.Width = Width;
    }

    private void HideWithoutClearingContent()
    {
        try
        {
            Hide();
        }
        catch (InvalidOperationException)
        {
            // Closing can dispose the HWND while asynchronous work completes.
        }
    }

    private void ResetOperationPresentation()
    {
        if (_isDragging)
        {
            _isDragging = false;
            TitleBar.ReleaseMouseCapture();
        }

        _isSubtitleMode = false;
        ApplyClickThrough(clickThrough: false);
        _subtitlePaused = false;
        _canReselect = false;
        ClearSubtitleCaptureSafety();
        ApplyFullCardPresentation();
        SubtitleControlBar.Visibility = Visibility.Collapsed;
        PauseResumeButton.Content = "暂停";
        AutomationProperties.SetName(
            PauseResumeButton,
            "暂停字幕翻译");
        ReselectButton.IsEnabled = false;
        HideAndClear();
    }

    private void BeginSubtitlePresentation(
        TranslationOverlayOperationToken operation)
    {
        ApplySubtitlePresentation();
        _subtitlePaused = false;
        _canReselect = false;
        ClearTranslationContent();
        SubtitleControlBar.Visibility = Visibility.Collapsed;
        PauseResumeButton.Content = "暂停";
        AutomationProperties.SetName(
            PauseResumeButton,
            "暂停字幕翻译");
        ReselectButton.IsEnabled = false;
        SubtitleNoticeTextBlock.Text = "字幕翻译准备中。";
        SubtitleNoticeTextBlock.Visibility = Visibility.Visible;
        SubtitleSurface.Visibility = Visibility.Visible;
        _ = PositionSubtitleSurfaceCore();
    }

    private void ShowAt(
        TranslationOverlayOperationToken operation,
        PixelPoint anchor,
        bool subtitleMode)
    {
        if (!IsCurrentOperation(operation))
            return;

        var reveal = !IsVisible;
        if (reveal)
            PrepareInitialReveal();
        if (!ShowWindowSafely())
            return;

        UpdateLayout();
        PositionWindow(subtitleMode, anchor);
        if (reveal)
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
        if (subtitleMode)
        {
            _ = PositionSubtitleSurfaceCore();
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0 || !TryGetWindowBounds(handle, out var bounds))
                return;

            var monitor = ResolveAnchorMonitor(anchor);
            if (monitor is null)
                return;

            var target = GetAnchorPosition(bounds, monitor.WorkArea, anchor);
            MoveWindow(handle, target);

            if (TryGetWindowBounds(handle, out var moved) &&
                (moved.Width != bounds.Width ||
                 moved.Height != bounds.Height))
            {
                target = GetAnchorPosition(
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
            var monitorId = !string.IsNullOrWhiteSpace(
                _subtitleRegionMonitorId)
                ? _subtitleRegionMonitorId
                : _settings.Translation.SubtitleOverlayPlacement?.MonitorId;
            if (!string.IsNullOrWhiteSpace(monitorId))
            {
                return monitors.FirstOrDefault(item =>
                    string.Equals(
                        item.DeviceName,
                        monitorId,
                        StringComparison.OrdinalIgnoreCase));
            }
            return DisplayTopology.SelectPrimaryMonitor(monitors);
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

    private static PixelRect ToPixelBounds(
        DisplayMonitor monitor,
        TranslationCaptureRegion region)
    {
        var monitorBounds = monitor.Bounds;
        var left = ClampCoordinate(
            monitorBounds.Left +
            (int)Math.Round(region.RelativeX * monitorBounds.Width),
            monitorBounds.Left,
            monitorBounds.Right - 1);
        var top = ClampCoordinate(
            monitorBounds.Top +
            (int)Math.Round(region.RelativeY * monitorBounds.Height),
            monitorBounds.Top,
            monitorBounds.Bottom - 1);
        var right = ClampCoordinate(
            left +
            Math.Max(
                1,
                (int)Math.Round(
                    region.RelativeWidth * monitorBounds.Width)),
            left + 1,
            monitorBounds.Right);
        var bottom = ClampCoordinate(
            top +
            Math.Max(
                1,
                (int)Math.Round(
                    region.RelativeHeight * monitorBounds.Height)),
            top + 1,
            monitorBounds.Bottom);
        return new PixelRect(
            left,
            top,
            right - left,
            bottom - top);
    }

    private static int ClampCoordinate(
        int value,
        int minimum,
        int maximum) =>
        maximum < minimum
            ? minimum
            : Math.Clamp(value, minimum, maximum);

    private static bool Intersects(PixelRect left, PixelRect right) =>
        left.Left < right.Right &&
        left.Right > right.Left &&
        left.Top < right.Bottom &&
        left.Bottom > right.Top;

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
        SubtitleTranslationTextBlock.Text = "";
        SubtitleNoticeTextBlock.Text = "";
        SubtitleNoticeTextBlock.Visibility = Visibility.Collapsed;
        TranslatedPanel.Visibility = Visibility.Collapsed;
        MetadataTextBlock.Text = "";
        OriginalPanel.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
    }

    private void HideAndClear()
    {
        ClearTranslationContent();
        StatusTextBlock.Text = "";
        SubtitleSurface.Visibility = Visibility.Collapsed;
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

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        var wasSubtitleMode = _isSubtitleMode;
        Dismiss();
        if (wasSubtitleMode)
            StopSubtitleRequested?.Invoke();
    }

    private void Dismiss()
    {
        var wasSubtitleMode = _isSubtitleMode;
        _dismissed = true;
        InvalidateActiveOperation();
        _isSubtitleMode = false;
        ApplyClickThrough(clickThrough: false);
        _subtitlePaused = false;
        _canReselect = false;
        ClearSubtitleCaptureSafety();
        _isDragging = false;
        SubtitleControlBar.Visibility = Visibility.Collapsed;
        HideAndClear();
        if (!wasSubtitleMode)
            InteractiveOperationDismissed?.Invoke();
    }

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
        if (!IsCaptureExcluded &&
            _subtitleCaptureRegion is { } captureRegion &&
            Intersects(
                new PixelRect(
                    target.X,
                    target.Y,
                    current.Width,
                    current.Height),
                captureRegion))
        {
            e.Handled = true;
            return;
        }
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

        ApplyExtendedWindowStyle(handle, _isClickThrough);
        IsCaptureExcluded = WindowCaptureExclusion.TryExclude(handle);
        if (_isSubtitleMode && IsVisible)
            PositionWindow(subtitleMode: true, default);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _isClosed = true;
        _dismissed = true;
        InvalidateActiveOperation();
        _isSubtitleMode = false;
        ApplyClickThrough(clickThrough: false);
        _isDragging = false;
        ClearSubtitleCaptureSafety();
        ClearTranslationContent();
        StatusTextBlock.Text = "";
        SubtitleSurface.Visibility = Visibility.Collapsed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        InvalidateActiveOperation();
        ClearTranslationContent();
        StatusTextBlock.Text = "";
        SubtitleSurface.Visibility = Visibility.Collapsed;
    }

    private bool IsCurrentOperation(
        TranslationOverlayOperationToken operation) =>
        !_isClosed &&
        !_dismissed &&
        operation.Generation != 0 &&
        operation.Generation == _activeOperationGeneration;

    private void InvalidateActiveOperation() =>
        _activeOperationGeneration =
            NextGeneration(_activeOperationGeneration);

    private void ApplyClickThrough(bool clickThrough)
    {
        _isClickThrough = clickThrough;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            ApplyExtendedWindowStyle(handle, clickThrough);
    }

    private static void ApplyExtendedWindowStyle(
        nint handle,
        bool clickThrough)
    {
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        style = clickThrough
            ? style | WS_EX_TRANSPARENT
            : style & ~WS_EX_TRANSPARENT;
        _ = SetWindowLongPtr(handle, GWL_EXSTYLE, new nint(style));
    }

    private void ClearSubtitleCaptureSafety()
    {
        _subtitleCaptureRegion = null;
        _subtitleRegionMonitorId = null;
    }

    private static long NextGeneration(long generation)
    {
        var next = generation + 1;
        return next <= 0 ? 1 : next;
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
