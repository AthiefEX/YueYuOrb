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
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

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
        var launchers = settings.Launchers.OrderBy(item => item.Order).ToArray();
        var wheelLaunchers = launchers.Where(item => item.ShowInWheel).ToArray();
        var dockLaunchers = launchers.Where(item => !item.ShowInWheel).ToArray();

        WheelLauncherPanel.Children.Clear();
        WheelLauncherScroller.Visibility = wheelLaunchers.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        for (var index = 0; index < wheelLaunchers.Length; index++)
        {
            var item = wheelLaunchers[index];
            var button = new Button
            {
                Name = $"WheelLauncherButton{index}",
                Width = 224,
                Height = 42,
                Margin = new Thickness(0, 0, 0, 6),
                Content = CreateWheelLauncherContent(item),
                Style = (Style)FindResource("DockLauncherButtonStyle"),
                Tag = item,
                ToolTip = item.Path
            };
            AutomationProperties.SetName(button, item.Name);
            button.Click += (_, _) => LaunchRequested?.Invoke(item);
            WheelLauncherPanel.Children.Add(button);
        }

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
    }
    public void SetRegionCaptureHotkey(string hotkey)
    {
        RegionCaptureButton.ToolTip = $"框选截图（{hotkey}）";
    }

    public void ShowNear(Window orb, PixelRect orbPixels)
    {
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
        Canvas.SetLeft(LauncherDock, (PanelWidth - LauncherDock.Width) / 2);
        Canvas.SetTop(LauncherDock, 488);
        Canvas.SetLeft(
            WheelLauncherScroller,
            opensLeft ? 22 : PanelWidth - 22 - WheelLauncherScroller.Width);
        Canvas.SetTop(WheelLauncherScroller, 68);

        var actions = new (Button Element, double ArcOffset)[]
        {
            (RegionCaptureButton, 18d),
            (WindowCaptureButton, 9d),
            (MonitorCaptureButton, 3d),
            (AiButton, 0d),
            (NotesButton, 3d),
            (InputLockButton, 9d),
            (SettingsButton, 18d)
        };
        const double columnX = 300d;
        const double actionWidth = 196d;
        const double actionHeight = 48d;
        const double rowStep = 55d;
        var direction = opensLeft ? -1 : 1;
        var totalHeight = ((actions.Length - 1) * rowStep) + actionHeight;
        var startY = Math.Clamp(
            anchor.Y - (totalHeight / 2),
            28d,
            448d - totalHeight);
        var centers = new List<Point>(actions.Length);
        for (var index = 0; index < actions.Length; index++)
        {
            var (element, arcOffset) = actions[index];
            var x = opensLeft
                ? columnX + arcOffset
                : PanelWidth - columnX - actionWidth - arcOffset;
            var y = startY + (index * rowStep);
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            centers.Add(new Point(x + 25, y + (actionHeight / 2)));
        }
        ArrangeAnchor(anchor);
        UpdateFanGeometry(anchor, direction);
        UpdateConnectorGeometry(anchor, centers);
    }

    private void ArrangeAnchor(Point anchor)
    {
        Canvas.SetLeft(AnchorHalo, anchor.X - AnchorHalo.Width / 2);
        Canvas.SetTop(AnchorHalo, anchor.Y - AnchorHalo.Height / 2);
        Canvas.SetLeft(AnchorRing, anchor.X - AnchorRing.Width / 2);
        Canvas.SetTop(AnchorRing, anchor.Y - AnchorRing.Height / 2);
        Canvas.SetLeft(
            AnchorOrbitSmall,
            anchor.X - AnchorOrbitSmall.Width / 2);
        Canvas.SetTop(
            AnchorOrbitSmall,
            anchor.Y - AnchorOrbitSmall.Height / 2);
        Canvas.SetLeft(
            AnchorOrbitLarge,
            anchor.X - AnchorOrbitLarge.Width / 2);
        Canvas.SetTop(
            AnchorOrbitLarge,
            anchor.Y - AnchorOrbitLarge.Height / 2);
    }

    private void UpdateFanGeometry(Point anchor, double direction)
    {
        static Point Clamp(Point point) =>
            new(
                Math.Clamp(point.X, 8, PanelWidth - 8),
                Math.Clamp(point.Y, 8, 452));

        var outerTop = Clamp(
            new Point(anchor.X + direction * 335, 36));
        var outerBottom = Clamp(
            new Point(anchor.X + direction * 335, 458));

        var wash = new StreamGeometry();
        using (var context = wash.Open())
        {
            context.BeginFigure(anchor, isFilled: true, isClosed: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 70, anchor.Y - 118)),
                Clamp(new Point(anchor.X + direction * 250, 22)),
                outerTop,
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 368, 150)),
                Clamp(new Point(anchor.X + direction * 368, 350)),
                outerBottom,
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 160, 402)),
                Clamp(new Point(anchor.X + direction * 32, 300)),
                anchor,
                isStroked: false,
                isSmoothJoin: true);
        }
        wash.Freeze();
        FanWash.Data = wash;

        var highlight = new StreamGeometry();
        using (var context = highlight.Open())
        {
            context.BeginFigure(anchor, isFilled: true, isClosed: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 55, anchor.Y - 88)),
                Clamp(new Point(anchor.X + direction * 215, 74)),
                Clamp(new Point(anchor.X + direction * 295, 72)),
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 318, 170)),
                Clamp(new Point(anchor.X + direction * 315, 345)),
                Clamp(new Point(anchor.X + direction * 295, 432)),
                isStroked: false,
                isSmoothJoin: true);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 145, 370)),
                Clamp(new Point(anchor.X + direction * 28, 292)),
                anchor,
                isStroked: false,
                isSmoothJoin: true);
        }
        highlight.Freeze();
        FanHighlight.Data = highlight;

        var rim = new StreamGeometry();
        using (var context = rim.Open())
        {
            context.BeginFigure(outerTop, isFilled: false, isClosed: false);
            context.BezierTo(
                Clamp(new Point(anchor.X + direction * 368, 150)),
                Clamp(new Point(anchor.X + direction * 368, 350)),
                outerBottom,
                isStroked: true,
                isSmoothJoin: true);
        }
        rim.Freeze();
        FanRim.Data = rim;
    }

    private void UpdateConnectorGeometry(
        Point anchor,
        IReadOnlyList<Point> centers)
    {
        var connectors = new StreamGeometry();
        using (var context = connectors.Open())
        {
            foreach (var center in centers)
            {
                context.BeginFigure(anchor, isFilled: false, isClosed: false);
                context.LineTo(center, isStroked: true, isSmoothJoin: true);
            }
        }
        connectors.Freeze();
        ConnectorPath.Data = connectors;
    }

    private void PlayEntranceAnimation()
    {
        MenuRoot.BeginAnimation(OpacityProperty, null);
        MenuScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        MenuScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!SystemParameters.ClientAreaAnimation)
        {
            MenuRoot.Opacity = 1;
            MenuScaleTransform.ScaleX = 1;
            MenuScaleTransform.ScaleY = 1;
            return;
        }

        MenuRoot.Opacity = 0;
        var opacity = new DoubleAnimation(
            0,
            1,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        var scale = new DoubleAnimation(
            0.96,
            1,
            TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        MenuRoot.BeginAnimation(OpacityProperty, opacity);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            scale);
        MenuScaleTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            scale);
    }

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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        var icon = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(
                Color.FromRgb(46, 139, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE71D",
                FontFamily = new FontFamily(
                    "Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brushes.White,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        grid.Children.Add(icon);
        var text = new TextBlock
        {
            Text = item.Name,
            Margin = new Thickness(8, 0, 0, 0),
            Foreground = new SolidColorBrush(
                Color.FromRgb(24, 52, 91)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 145
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
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
    private void OnWheelLauncherMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (WheelLauncherScroller.ScrollableHeight <= 0)
            return;
        WheelLauncherScroller.ScrollToVerticalOffset(
            WheelLauncherScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

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
