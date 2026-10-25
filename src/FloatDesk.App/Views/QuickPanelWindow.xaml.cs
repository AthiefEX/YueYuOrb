using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow : Window
{
    private const double PanelWidth = 660;
    private const double PanelHeight = 560;
    private const double WheelViewportWidth = 380;
    private const double WheelViewportHeight = 420;
    private const double WheelButtonWidth = 196;
    private const double WheelButtonHeight = 48;
    private const double WheelRowStep = 55;
    private const double MaximumWheelArcOffset = 84;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private Point _wheelAnchor;
    private bool _wheelOpensLeft;
    private bool _wheelLayoutReady;
    private bool _updatingWheelLayout;
    private bool _hideTransitionPending;

    public event Action<LauncherItem>? LaunchRequested;
    public event Action? SettingsRequested;
    public event Action? InputLockToggleRequested;
    public event Action? AiRequested;
    public event Action? NotesRequested;
    public event Action<FloatDesk.Core.Models.CaptureMode>? CaptureRequested;

    public QuickPanelWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void Apply(AppSettings settings, bool manualLockEnabled, string inputStatus)
    {
        InputLockTitle.Text = manualLockEnabled ? "停止锁定" : "锁定英文";
        InputLockGlyph.Text = manualLockEnabled ? "\uE785" : "\uE72E";
        InputLockGlyphHost.Background = (Brush)FindResource(
            manualLockEnabled ? "MenuGreenBrush" : "MenuBlueBrush");

        var enabledWheelActions = settings.WheelActions.ToHashSet();
        foreach (var (button, kind) in GetFixedWheelActions())
        {
            button.Visibility = enabledWheelActions.Contains(kind)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        var launchers = settings.Launchers.OrderBy(item => item.Order).ToArray();
        var wheelLaunchers = launchers.Where(item => item.ShowInWheel).ToArray();
        var dockLaunchers = launchers.Where(item => !item.ShowInWheel).ToArray();

        foreach (var button in WheelCanvas.Children
                     .OfType<Button>()
                     .Where(button => button.Tag is LauncherItem)
                     .ToArray())
        {
            WheelCanvas.Children.Remove(button);
        }
        for (var index = 0; index < wheelLaunchers.Length; index++)
        {
            var item = wheelLaunchers[index];
            var button = new Button
            {
                Name = $"WheelLauncherButton{index}",
                Width = WheelButtonWidth,
                Height = WheelButtonHeight,
                Content = CreateWheelLauncherContent(item),
                Style = (Style)FindResource("RadialActionButtonStyle"),
                Tag = item,
                ToolTip = item.Path
            };
            AutomationProperties.SetName(button, item.Name);
            button.Click += (_, _) => LaunchRequested?.Invoke(item);
            WheelCanvas.Children.Add(button);
        }
        WheelScroller.Visibility = GetVisibleWheelButtons().Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        WheelScroller.ScrollToVerticalOffset(0);

        LauncherPanel.Children.Clear();
        LauncherDock.Visibility = dockLaunchers.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        LauncherCountText.Text = $"{dockLaunchers.Length} 个项目";
        foreach (var item in dockLaunchers)
        {
            var button = new Button
            {
                Content = CreateLauncherContent(item.Name),
                Style = (Style)FindResource("DockLauncherButtonStyle"),
                ToolTip = item.Path
            };
            AutomationProperties.SetName(button, item.Name);
            button.Click += (_, _) => LaunchRequested?.Invoke(item);
            LauncherPanel.Children.Add(button);
        }

        if (dockLaunchers.Length > 0)
        {
            var addButton = new Button
            {
                Content = CreateAddContent(),
                Style = (Style)FindResource("DockLauncherButtonStyle"),
                ToolTip = "打开设置并添加快捷应用"
            };
            AutomationProperties.SetName(addButton, "添加应用");
            addButton.Click += (_, _) => SettingsRequested?.Invoke();
            LauncherPanel.Children.Add(addButton);
        }

        if (_wheelLayoutReady)
            ArrangeMenu(_wheelAnchor, _wheelOpensLeft);
    }
    public void SetRegionCaptureHotkey(string hotkey)
    {
        RegionCaptureButton.ToolTip = $"框选截图（{hotkey}）";
    }

    public void ShowNear(Window orb, PixelRect orbPixels)
    {
        CancelPendingHide();
        ApplyFixedSize();
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
        SetWindowPos(
            handle,
            0,
            point.X,
            point.Y,
            pixels.Width,
            pixels.Height,
            0x0004 | 0x0010);
        UpdateLayout();

        var orbCenter = new Point(
            orbPixels.Left + orbPixels.Width / 2.0,
            orbPixels.Top + orbPixels.Height / 2.0);
        var anchor = PointFromScreen(orbCenter);
        ArrangeMenu(anchor, opensLeft: point.X < orbPixels.Left);
        PlayEntranceAnimation();
        Activate();
        Focus();
    }

    public void HideAnimated()
    {
        if (!IsVisible || _hideTransitionPending)
            return;
        if (!SystemParameters.ClientAreaAnimation)
        {
            ResetMotionVisuals();
            Hide();
            return;
        }

        _hideTransitionPending = true;
        IsHitTestVisible = false;
        var ease = new CubicEase
        {
            EasingMode = EasingMode.EaseIn
        };
        var opacity = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(145),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var scale = new DoubleAnimation
        {
            To = 0.975,
            Duration = TimeSpan.FromMilliseconds(165),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        var translate = new DoubleAnimation
        {
            To = _wheelOpensLeft ? 10 : -10,
            Duration = TimeSpan.FromMilliseconds(165),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        opacity.Completed += (_, _) =>
        {
            if (!_hideTransitionPending)
                return;
            _hideTransitionPending = false;
            ResetMotionVisuals();
            Hide();
        };
        MenuRoot.BeginAnimation(OpacityProperty, opacity);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            scale);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            scale);
        MenuTranslateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            translate);
    }

    private void CancelPendingHide()
    {
        _hideTransitionPending = false;
        IsHitTestVisible = true;
        ResetMotionVisuals();
    }

    public void RefreshVisibility() => ShowNear(Owner ?? this, new PixelRect(
        (int)Left,
        (int)Top,
        (int)Width,
        (int)Height));

    private void ApplyFixedSize()
    {
        SizeToContent = SizeToContent.Manual;
        Width = PanelWidth;
        Height = PanelHeight;
    }

    private void ArrangeMenu(Point anchor, bool opensLeft)
    {
        _wheelAnchor = anchor;
        _wheelOpensLeft = opensLeft;
        _wheelLayoutReady = true;

        Canvas.SetLeft(LauncherDock, (PanelWidth - LauncherDock.Width) / 2);
        Canvas.SetTop(LauncherDock, 488);
        Canvas.SetLeft(
            WheelScroller,
            opensLeft
                ? PanelWidth - 22 - WheelViewportWidth
                : 22);
        Canvas.SetTop(
            WheelScroller,
            Math.Clamp(
                anchor.Y - (WheelViewportHeight / 2),
                20,
                68));

        LayoutWheelEntries(resetScroll: true);
        ArrangeAnchor(anchor);
        UpdateFanGeometry(
            anchor,
            opensLeft ? -1 : 1,
            GetVisibleWheelButtons());
    }

    private (Button Element, WheelActionKind Kind)[] GetFixedWheelActions() =>
    [
        (RegionCaptureButton, WheelActionKind.RegionCapture),
        (WindowCaptureButton, WheelActionKind.WindowCapture),
        (MonitorCaptureButton, WheelActionKind.MonitorCapture),
        (AiButton, WheelActionKind.AiBrowser),
        (NotesButton, WheelActionKind.Notes),
        (InputLockButton, WheelActionKind.InputLock),
        (SettingsButton, WheelActionKind.Settings)
    ];

    private Button[] GetVisibleWheelButtons()
    {
        var fixedButtons = GetFixedWheelActions()
            .Select(item => item.Element)
            .Where(button => button.Visibility == Visibility.Visible);
        var launcherButtons = WheelCanvas.Children
            .OfType<Button>()
            .Where(button =>
                button.Tag is LauncherItem &&
                button.Visibility == Visibility.Visible);
        return [.. fixedButtons, .. launcherButtons];
    }

    private void LayoutWheelEntries(bool resetScroll)
    {
        if (_updatingWheelLayout)
            return;

        _updatingWheelLayout = true;
        try
        {
            var buttons = GetVisibleWheelButtons();
            WheelScroller.Visibility = buttons.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (buttons.Length == 0)
            {
                WheelCanvas.Height = WheelViewportHeight;
                return;
            }

            var contentHeight =
                ((buttons.Length - 1) * WheelRowStep) +
                WheelButtonHeight;
            var canvasHeight = Math.Max(WheelViewportHeight, contentHeight + 32);
            var startY = (canvasHeight - contentHeight) / 2;
            WheelCanvas.Height = canvasHeight;
            for (var index = 0; index < buttons.Length; index++)
            {
                Canvas.SetTop(buttons[index], startY + (index * WheelRowStep));
                PositionWheelButton(buttons[index]);
            }

            if (resetScroll)
                WheelScroller.ScrollToVerticalOffset(0);
        }
        finally
        {
            _updatingWheelLayout = false;
        }
    }

    private void UpdateWheelForScroll()
    {
        if (!_wheelLayoutReady || _updatingWheelLayout)
            return;

        _updatingWheelLayout = true;
        try
        {
            var buttons = GetVisibleWheelButtons();
            foreach (var button in buttons)
                PositionWheelButton(button);
            if (WheelScroller.Visibility == Visibility.Visible)
            {
                UpdateActionBeamGeometry(
                    _wheelAnchor,
                    _wheelOpensLeft ? -1 : 1,
                    buttons);
            }
        }
        finally
        {
            _updatingWheelLayout = false;
        }
    }

    private void PositionWheelButton(Button button)
    {
        var viewportCenter =
            WheelScroller.VerticalOffset + (WheelViewportHeight / 2);
        var buttonCenter = Canvas.GetTop(button) + (WheelButtonHeight / 2);
        var distance = Math.Clamp(
            Math.Abs(buttonCenter - viewportCenter) /
            (WheelViewportHeight / 2),
            0,
            1);
        var easedDistance = distance * distance * (3 - (2 * distance));
        var arcOffset = MaximumWheelArcOffset * easedDistance;
        var farX = _wheelOpensLeft ? 42 : 142;
        var direction = _wheelOpensLeft ? -1 : 1;
        Canvas.SetLeft(button, farX - (direction * arcOffset));
    }

    private void ArrangeAnchor(Point anchor)
    {
        Canvas.SetLeft(AnchorHalo, anchor.X - AnchorHalo.Width / 2);
        Canvas.SetTop(AnchorHalo, anchor.Y - AnchorHalo.Height / 2);
        Canvas.SetLeft(
            AnchorOrbitOuter,
            anchor.X - AnchorOrbitOuter.Width / 2);
        Canvas.SetTop(
            AnchorOrbitOuter,
            anchor.Y - AnchorOrbitOuter.Height / 2);
        Canvas.SetLeft(
            AnchorOrbitInner,
            anchor.X - AnchorOrbitInner.Width / 2);
        Canvas.SetTop(
            AnchorOrbitInner,
            anchor.Y - AnchorOrbitInner.Height / 2);
    }

    private void UpdateFanGeometry(
        Point anchor,
        double direction,
        Button[] actions)
    {
        const double edge = 10;
        const double contentGap = 12;
        if (actions.Length == 0 ||
            WheelScroller.Visibility != Visibility.Visible)
        {
            FanWash.Data = Geometry.Empty;
            ActionBeamsPath.Data = Geometry.Empty;
            WheelScroller.Clip = null;
            return;
        }

        var wheelLeft = Canvas.GetLeft(WheelScroller);
        var wheelTop = Canvas.GetTop(WheelScroller);
        var top = wheelTop;
        var bottom = wheelTop + WheelViewportHeight;
        var actionCurveEdge = direction > 0
            ? wheelLeft
            : wheelLeft + WheelViewportWidth;
        var farX = direction > 0
            ? wheelLeft + WheelViewportWidth
            : wheelLeft;

        top = Math.Clamp(top - contentGap, 8, PanelHeight - 80);
        bottom = Math.Clamp(
            bottom + contentGap,
            top + 80,
            PanelHeight - 8);
        farX = direction > 0
            ? Math.Clamp(farX + (contentGap * 1.35), edge, PanelWidth - edge)
            : Math.Clamp(farX - (contentGap * 1.35), edge, PanelWidth - edge);
        var distance = Math.Abs(farX - anchor.X);
        var shoulderX = actionCurveEdge + (direction * 16);
        var corner = Math.Min(38, Math.Max(22, distance * 0.11));
        var outerTop = top + Math.Min(32, Math.Max(16, distance * 0.05));
        var nearBottom = bottom - 10;

        var wash = new StreamGeometry();
        using (var context = wash.Open())
        {
            context.BeginFigure(anchor, isFilled: true, isClosed: true);
            context.BezierTo(
                new Point(anchor.X + (direction * 24), anchor.Y - 104),
                new Point(anchor.X + (direction * 48), top),
                new Point(shoulderX, top),
                isStroked: false,
                isSmoothJoin: true);
            context.LineTo(
                new Point(farX - (direction * corner), outerTop),
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                new Point(farX - (direction * 10), outerTop),
                new Point(farX, outerTop + 10),
                new Point(farX, outerTop + corner),
                isStroked: false,
                isSmoothJoin: true);
            context.LineTo(
                new Point(farX, bottom - corner),
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                new Point(farX, bottom - 10),
                new Point(farX - (direction * 10), bottom),
                new Point(farX - (direction * corner), bottom),
                isStroked: false,
                isSmoothJoin: true);
            context.LineTo(
                new Point(shoulderX, nearBottom),
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                new Point(anchor.X + (direction * 48), bottom),
                new Point(anchor.X + (direction * 24), anchor.Y + 116),
                anchor,
                isStroked: false,
                isSmoothJoin: true);
        }
        wash.Freeze();
        FanWash.Data = wash;
        UpdateActionBeamGeometry(anchor, direction, actions);
        UpdateWheelDrawerClip(wash);
    }

    private void UpdateActionBeamGeometry(
        Point anchor,
        double direction,
        Button[] actions)
    {
        var beams = new StreamGeometry();
        using (var context = beams.Open())
        {
            foreach (var action in actions)
            {
                var bounds = GetWheelButtonMenuBounds(action);
                if (!IsInsideWheelViewport(bounds))
                    continue;
                var targetX = direction > 0
                    ? bounds.Left
                    : bounds.Right;
                var targetY = bounds.Top + (bounds.Height / 2);
                context.BeginFigure(anchor, isFilled: true, isClosed: true);
                context.LineTo(
                    new Point(targetX, targetY - 18),
                    isStroked: false,
                    isSmoothJoin: true);
                context.LineTo(
                    new Point(targetX, targetY + 18),
                    isStroked: false,
                    isSmoothJoin: true);
            }
        }
        beams.Freeze();
        ActionBeamsPath.Data = beams;
    }

    private Rect GetWheelButtonMenuBounds(FrameworkElement button) =>
        new(
            Canvas.GetLeft(WheelScroller) +
            Canvas.GetLeft(button) -
            WheelScroller.HorizontalOffset,
            Canvas.GetTop(WheelScroller) +
            Canvas.GetTop(button) -
            WheelScroller.VerticalOffset,
            button.Width,
            button.Height);

    private bool IsInsideWheelViewport(Rect bounds)
    {
        var viewportTop = Canvas.GetTop(WheelScroller);
        var viewportBottom = viewportTop + WheelViewportHeight;
        return bounds.Bottom >= viewportTop + 8 &&
               bounds.Top <= viewportBottom - 8;
    }

    private void UpdateWheelDrawerClip(Geometry fan)
    {
        if (WheelScroller.Visibility != Visibility.Visible)
        {
            WheelScroller.Clip = null;
            return;
        }

        var left = Canvas.GetLeft(WheelScroller);
        var top = Canvas.GetTop(WheelScroller);
        var viewport = new Rect(
            left,
            top,
            WheelViewportWidth,
            WheelViewportHeight);
        var clipped = new CombinedGeometry(
            GeometryCombineMode.Intersect,
            fan,
            new RectangleGeometry(viewport, 18, 18))
        {
            Transform = new TranslateTransform(-left, -top)
        };
        clipped.Freeze();
        WheelScroller.Clip = clipped;
    }

    private void PlayEntranceAnimation()
    {
        _hideTransitionPending = false;
        IsHitTestVisible = true;
        ResetMotionVisuals();
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var outEase = new QuinticEase
        {
            EasingMode = EasingMode.EaseOut
        };
        MenuRoot.Opacity = 0;
        MenuScaleTransform.ScaleX = 0.975;
        MenuScaleTransform.ScaleY = 0.975;
        MenuTranslateTransform.X = _wheelOpensLeft ? 8 : -8;
        MenuRoot.BeginAnimation(
            OpacityProperty,
            MotionAnimation(0, 1, 170, 0, outEase));
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            MotionAnimation(0.975, 1, 230, 0, outEase));
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            MotionAnimation(0.975, 1, 230, 0, outEase));
        MenuTranslateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            MotionAnimation(
                _wheelOpensLeft ? 8 : -8,
                0,
                220,
                0,
                outEase));

        ActionBeamsPath.Opacity = 0;
        ActionBeamsPath.BeginAnimation(
            OpacityProperty,
            MotionAnimation(0, 1, 210, 25, outEase));
        AnchorHalo.Opacity = 0;
        AnchorHalo.BeginAnimation(
            OpacityProperty,
            MotionAnimation(0, 1, 240, 0, outEase));

        if (LauncherDock.Visibility == Visibility.Visible)
        {
            var dockOffset = new TranslateTransform(0, 9);
            LauncherDock.RenderTransform = dockOffset;
            LauncherDock.Opacity = 0;
            LauncherDock.BeginAnimation(
                OpacityProperty,
                MotionAnimation(0, 1, 190, 40, outEase));
            dockOffset.BeginAnimation(
                TranslateTransform.YProperty,
                MotionAnimation(9, 0, 210, 40, outEase));
        }

        var items = GetVisibleWheelButtons()
            .Where(button =>
                IsInsideWheelViewport(
                    GetWheelButtonMenuBounds(button)))
            .OrderBy(button =>
            {
                var bounds = GetWheelButtonMenuBounds(button);
                var centerY = bounds.Top + (bounds.Height / 2);
                return Math.Abs(centerY - _wheelAnchor.Y);
            })
            .Take(10)
            .ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var delay = index * 18;
            var button = items[index];
            var (scale, translate) = ConfigureWheelItemMotion(button);
            button.BeginAnimation(
                OpacityProperty,
                MotionAnimation(0, 1, 175, delay, outEase));
            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                MotionAnimation(0.9, 1, 235, delay, outEase));
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                MotionAnimation(0.9, 1, 235, delay, outEase));
            translate.BeginAnimation(
                TranslateTransform.XProperty,
                MotionAnimation(
                    translate.X,
                    0,
                    250,
                    delay,
                    outEase));
            translate.BeginAnimation(
                TranslateTransform.YProperty,
                MotionAnimation(
                    translate.Y,
                    0,
                    250,
                    delay,
                    outEase));
        }
    }

    private void ResetMotionVisuals()
    {
        MenuRoot.BeginAnimation(OpacityProperty, null);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            null);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            null);
        MenuTranslateTransform.BeginAnimation(
            TranslateTransform.XProperty,
            null);
        ActionBeamsPath.BeginAnimation(OpacityProperty, null);
        AnchorHalo.BeginAnimation(OpacityProperty, null);
        LauncherDock.BeginAnimation(OpacityProperty, null);

        MenuRoot.Opacity = 1;
        MenuScaleTransform.ScaleX = 1;
        MenuScaleTransform.ScaleY = 1;
        MenuTranslateTransform.X = 0;
        ActionBeamsPath.Opacity = 1;
        AnchorHalo.Opacity = 1;
        LauncherDock.Opacity = 1;
        LauncherDock.RenderTransform = new TranslateTransform();

        foreach (var button in GetVisibleWheelButtons())
        {
            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = 1;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(1, 1),
                    new TranslateTransform()
                }
            };
        }
    }

    private (ScaleTransform Scale, TranslateTransform Translate)
        ConfigureWheelItemMotion(Button button)
    {
        var bounds = GetWheelButtonMenuBounds(button);
        var centerX = bounds.Left + (bounds.Width / 2);
        var centerY = bounds.Top + (bounds.Height / 2);
        var towardAnchorX = _wheelAnchor.X - centerX;
        var towardAnchorY = _wheelAnchor.Y - centerY;
        var length = Math.Sqrt(
            (towardAnchorX * towardAnchorX) +
            (towardAnchorY * towardAnchorY));
        var startX = length < 0.001
            ? _wheelOpensLeft ? 14 : -14
            : (towardAnchorX / length) * 14;
        var startY = length < 0.001
            ? 0
            : (towardAnchorY / length) * 14;
        var scale = new ScaleTransform(0.9, 0.9);
        var translate = new TranslateTransform(startX, startY);
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = new TransformGroup
        {
            Children = { scale, translate }
        };
        button.Opacity = 0;
        return (scale, translate);
    }

    private static DoubleAnimation MotionAnimation(
        double from,
        double to,
        int durationMilliseconds,
        int delayMilliseconds,
        EasingFunctionBase easing) =>
        new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delayMilliseconds),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };

    private static Grid CreateLauncherContent(string label)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var icon = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(
                Color.FromRgb(46, 139, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE71D",
                FontFamily = new FontFamily(
                    "Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brushes.White,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(icon);
        var text = new TextBlock
        {
            Text = label,
            Margin = new Thickness(7, 0, 0, 0),
            Foreground = new SolidColorBrush(
                Color.FromRgb(24, 52, 91)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 66
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static Grid CreateWheelLauncherContent(LauncherItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var icon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(21),
            Background = new LinearGradientBrush(
                Color.FromRgb(82, 177, 255),
                Color.FromRgb(39, 113, 235),
                45),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE71D",
                FontFamily = new FontFamily(
                    "Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brushes.White,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(icon);
        var label = new Border
        {
            Margin = new Thickness(6, 3, 0, 3),
            Background = new SolidColorBrush(
                Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(14, 0, 8, 0),
            Child = null
        };
        var text = new TextBlock
        {
            Text = item.Name,
            Foreground = new SolidColorBrush(
                Color.FromRgb(24, 52, 91)),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.Child = text;
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        return grid;
    }
    private static TextBlock CreateAddContent() =>
        new TextBlock
        {
            Text = "+ 添加应用",
            Foreground = new SolidColorBrush(
                Color.FromRgb(46, 139, 255)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        };

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        HwndSource.FromHwnd(
            new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }
    private void OnWheelMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (WheelScroller.ScrollableHeight <= 0)
            return;
        WheelScroller.ScrollToVerticalOffset(
            WheelScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnWheelScrollChanged(
        object sender,
        ScrollChangedEventArgs e) =>
        UpdateWheelForScroll();

    private void OnLauncherMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (LauncherScroller.ScrollableWidth <= 0)
            return;
        LauncherScroller.ScrollToHorizontalOffset(
            LauncherScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest)
            return 0;
        var packed = lParam.ToInt64();
        var point = PointFromScreen(new Point(
            (short)(packed & 0xFFFF),
            (short)((packed >> 16) & 0xFFFF)));
        if (point.X < 0 ||
            point.Y < 0 ||
            point.X >= ActualWidth ||
            point.Y >= ActualHeight)
        {
            return 0;
        }
        var hit = InputHitTest(point) as DependencyObject;
        if (hit is null ||
            ReferenceEquals(hit, this) ||
            ReferenceEquals(hit, MenuRoot) ||
            ReferenceEquals(hit, MenuCanvas) ||
            hit is System.Windows.Shapes.Path)
        {
            handled = true;
            return new nint(HtTransparent);
        }
        return 0;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke();

    private void OnInputLockClick(object sender, RoutedEventArgs e) =>
        InputLockToggleRequested?.Invoke();

    private void OnAiClick(object sender, RoutedEventArgs e) =>
        AiRequested?.Invoke();

    private void OnNotesClick(object sender, RoutedEventArgs e) =>
        NotesRequested?.Invoke();

    private void OnRegionCaptureClick(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.Region);

    private void OnWindowCaptureClick(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.ActiveWindow);

    private void OnMonitorCaptureClick(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(FloatDesk.Core.Models.CaptureMode.CurrentMonitor);

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
