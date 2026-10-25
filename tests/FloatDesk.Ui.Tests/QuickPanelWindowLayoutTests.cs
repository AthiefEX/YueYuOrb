using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class QuickPanelWindowLayoutTests
{
    private static readonly string[] FixedActionNames =
    [
        "RegionCaptureButton",
        "WindowCaptureButton",
        "MonitorCaptureButton",
        "AiButton",
        "NotesButton",
        "InputLockButton",
        "SettingsButton"
    ];
    private static readonly string[] OrbitNames =
    [
        "AnchorOrbitOuter",
        "AnchorOrbitInner"
    ];

    [Fact(Timeout = 10_000)]
    public async Task ActionsUseUniformGeometryWithoutSelectionBorder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                foreach (var name in FixedActionNames)
                {
                    var button = Assert.IsType<Button>(
                        window.FindName(name));
                    Assert.Equal(196, button.Width);
                    Assert.Equal(48, button.Height);
                    Assert.Null(button.FocusVisualStyle);
                }

                AssertNoKeyboardFocusTrigger(
                    window,
                    "RadialActionButtonStyle");
                AssertNoKeyboardFocusTrigger(
                    window,
                    "DockLauncherButtonStyle");

            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task EmptyDockHidesAndTopLeftCardsAreRemoved()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");

                Assert.Null(window.FindName("BrandChip"));
                Assert.Null(window.FindName("StatusChip"));

                var dock = Assert.IsType<Border>(
                    window.FindName("LauncherDock"));
                Assert.Equal(Visibility.Collapsed, dock.Visibility);
                Assert.Equal(480, dock.Width);
                Assert.Equal(64, dock.Height);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task WheelAppsUseUnifiedWheelAndOnlyDockItemsOpenDock()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                var launchers = Enumerable.Range(0, 16)
                    .Select(index => new LauncherItem(
                        Guid.NewGuid(),
                        $"应用 {index + 1}",
                        LauncherKind.Exe,
                        $@"C:\Apps\app{index}.exe",
                        [],
                        null,
                        index,
                        ShowInWheel: true))
                    .ToList();
                launchers.Add(new LauncherItem(
                    Guid.NewGuid(),
                    "应用坞",
                    LauncherKind.Exe,
                    @"C:\Apps\dock.exe",
                    [],
                    null,
                    launchers.Count));
                window.Apply(
                    AppSettings.CreateDefault() with
                    {
                        Launchers = launchers
                    },
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(
                    window,
                    new Point(37, 280),
                    opensLeft: false);
                window.Show();
                window.UpdateLayout();

                var scroller = Assert.IsType<ScrollViewer>(
                    window.FindName("WheelScroller"));
                var canvas = Assert.IsType<Canvas>(
                    window.FindName("WheelCanvas"));
                var dock = Assert.IsType<Border>(
                    window.FindName("LauncherDock"));

                Assert.Equal(Visibility.Visible, scroller.Visibility);
                Assert.Equal(
                    23,
                    canvas.Children
                        .OfType<Button>()
                        .Count(button => button.Visibility == Visibility.Visible));
                Assert.Equal(Visibility.Visible, dock.Visibility);

                scroller.Measure(new Size(380, 420));
                scroller.Arrange(new Rect(0, 0, 380, 420));
                scroller.UpdateLayout();
                Assert.True(
                    scroller.ScrollableHeight > 0,
                    "Overflowing wheel applications must be scrollable.");

                window.Apply(
                    AppSettings.CreateDefault() with
                    {
                        Launchers = launchers
                            .Where(item => item.ShowInWheel)
                            .ToArray()
                    },
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                Assert.Equal(Visibility.Collapsed, dock.Visibility);
                window.Hide();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task WheelMenuHasNoBackgroundGuideLines()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                Assert.Null(window.FindName("ConnectorPath"));
                Assert.Null(window.FindName("FanHighlight"));
                Assert.Null(window.FindName("FanRim"));
                Assert.Null(window.FindName("AnchorRing"));
                Assert.Null(window.FindName("AnchorOrbitSmall"));
                Assert.Null(window.FindName("AnchorOrbitLarge"));

                var action = Assert.IsType<Button>(
                    window.FindName("RegionCaptureButton"));
                var content = Assert.IsType<Grid>(action.Content);
                var borders = content.Children.OfType<Border>().ToArray();
                Assert.Equal(2, borders.Length);
                Assert.All(
                    borders,
                    border => Assert.Equal(
                        new Thickness(0),
                        border.BorderThickness));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanBackdropContainsWheelAndLeavesDockOutside(
        bool opensLeft)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                var launchers = new[]
                {
                    new LauncherItem(
                        Guid.NewGuid(),
                        "轮盘应用",
                        LauncherKind.Exe,
                        @"C:\Apps\wheel.exe",
                        [],
                        null,
                        0,
                        ShowInWheel: true),
                    new LauncherItem(
                        Guid.NewGuid(),
                        "底栏应用",
                        LauncherKind.Exe,
                        @"C:\Apps\dock.exe",
                        [],
                        null,
                        1)
                };
                window.Apply(
                    AppSettings.CreateDefault() with
                    {
                        Launchers = launchers
                    },
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(
                    window,
                    new Point(opensLeft ? 623 : 37, 280),
                    opensLeft);

                var fan = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("FanWash"));
                var scroller = Assert.IsType<ScrollViewer>(
                    window.FindName("WheelScroller"));
                var geometry = Assert.IsAssignableFrom<Geometry>(fan.Data);
                Assert.Null(fan.Stroke);
                Assert.NotNull(scroller.Clip);
                Assert.IsType<LinearGradientBrush>(
                    scroller.OpacityMask);
                Assert.True(
                    geometry.Bounds.Height <= 480,
                    "The fan must hug the wheel instead of covering " +
                    "the complete 560-pixel panel.");
                var taperedCorner = new Point(
                    opensLeft ? geometry.Bounds.Left + 20 : geometry.Bounds.Right - 20,
                    geometry.Bounds.Top + 8);
                Assert.False(
                    geometry.FillContains(taperedCorner),
                    "The outer edge must taper toward the anchor like a fan.");
                string[] surfaces =
                [
                    "RegionCaptureButton",
                    "WindowCaptureButton",
                    "MonitorCaptureButton",
                    "AiButton",
                    "NotesButton",
                    "InputLockButton",
                    "SettingsButton"
                ];
                foreach (var name in surfaces)
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(
                        window.FindName(name));
                    var bounds = WheelSurfaceBounds(window, element, inset: 4);
                    Assert.True(
                        geometry.FillContains(bounds.TopLeft) &&
                        geometry.FillContains(bounds.TopRight) &&
                        geometry.FillContains(bounds.BottomLeft) &&
                        geometry.FillContains(bounds.BottomRight),
                        $"{name} must be inside the continuous glass backdrop.");
                }

                var dock = Assert.IsType<Border>(
                    window.FindName("LauncherDock"));
                var dockCenter = SurfaceBounds(dock, inset: 4);
                Assert.False(
                    geometry.FillContains(
                        new Point(
                            dockCenter.Left + (dockCenter.Width / 2),
                            dockCenter.Top + (dockCenter.Height / 2))),
                    "The separate quick-app dock must keep its own surface.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyWheelFanCollapsesToActionArc(bool opensLeft)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(
                    window,
                    new Point(opensLeft ? 623 : 37, 280),
                    opensLeft);

                var fan = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("FanWash"));
                var geometry = Assert.IsAssignableFrom<Geometry>(fan.Data);
                Assert.True(
                    geometry.Bounds.Width < 400,
                    "An empty wheel should not keep the full-width " +
                    "application drawer surface.");
                Assert.True(
                    geometry.Bounds.Height < 450,
                    "An empty wheel should only cover its action arc.");

                foreach (var name in FixedActionNames)
                {
                    var button = Assert.IsAssignableFrom<FrameworkElement>(
                        window.FindName(name));
                    var bounds = WheelSurfaceBounds(window, button, inset: 4);
                    Assert.True(
                        geometry.FillContains(bounds.TopLeft) &&
                        geometry.FillContains(bounds.TopRight) &&
                        geometry.FillContains(bounds.BottomLeft) &&
                        geometry.FillContains(bounds.BottomRight),
                        $"{name} must remain inside the compact fan.");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WheelUsesRadialBeamsOrbitsAndCurvedActions(
        bool opensLeft)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(
                    window,
                    new Point(opensLeft ? 623 : 37, 280),
                    opensLeft);

                var beams = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("ActionBeamsPath"));
                Assert.Null(beams.Stroke);
                Assert.IsAssignableFrom<Geometry>(beams.Data);

                var halo = Assert.IsType<System.Windows.Shapes.Ellipse>(
                    window.FindName("AnchorHalo"));
                Assert.True(halo.Width >= 180);
                foreach (var name in OrbitNames)
                {
                    var orbit = Assert.IsType<
                        System.Windows.Shapes.Ellipse>(
                        window.FindName(name));
                    Assert.Null(orbit.Stroke);
                }

                var top = Assert.IsType<Button>(
                    window.FindName("RegionCaptureButton"));
                var middle = Assert.IsType<Button>(
                    window.FindName("AiButton"));
                Assert.True(
                    Math.Abs(
                        Canvas.GetLeft(middle) -
                        Canvas.GetLeft(top)) >= 40,
                    "Actions must follow a visibly curved radial arc.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task WheelKeepsRadialBeamsWithoutVisibleFanBackdrop()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(
                    window,
                    new Point(37, 280),
                    opensLeft: false);

                var fan = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("FanWash"));
                var beams = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("ActionBeamsPath"));

                Assert.Equal(Visibility.Collapsed, fan.Visibility);
                Assert.NotNull(beams.Fill);
                Assert.NotEqual(Visibility.Collapsed, beams.Visibility);
                Assert.False(
                    Assert.IsAssignableFrom<Geometry>(beams.Data).Bounds.IsEmpty,
                    "The radial white pointer beams must remain visible.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task FunctionsAndApplicationsShareOneScrollableWheel()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                var wheelApps = Enumerable.Range(0, 16)
                    .Select(index => new LauncherItem(
                        Guid.NewGuid(),
                        $"轮盘应用 {index + 1}",
                        LauncherKind.Exe,
                        $@"C:\Apps\wheel{index}.exe",
                        [],
                        null,
                        index,
                        ShowInWheel: true))
                    .ToList();
                var dockApp = new LauncherItem(
                    Guid.NewGuid(),
                    "底栏应用",
                    LauncherKind.Exe,
                    @"C:\Apps\dock.exe",
                    [],
                    null,
                    wheelApps.Count);
                window.Apply(
                    AppSettings.CreateDefault() with
                    {
                        Launchers = [.. wheelApps, dockApp]
                    },
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(window, new Point(37, 280), opensLeft: false);
                window.Show();
                window.UpdateLayout();
                window.Measure(new Size(660, 560));
                window.Arrange(new Rect(0, 0, 660, 560));
                window.UpdateLayout();

                Assert.Null(window.FindName("WheelLauncherScroller"));
                Assert.Null(window.FindName("WheelLauncherPanel"));
                var scroller = Assert.IsType<ScrollViewer>(
                    window.FindName("WheelScroller"));
                var canvas = Assert.IsType<Canvas>(
                    window.FindName("WheelCanvas"));
                scroller.ApplyTemplate();
                var wheelButtons = canvas.Children
                    .OfType<Button>()
                    .Where(button => button.Visibility == Visibility.Visible)
                    .ToArray();

                Assert.Equal(23, wheelButtons.Length);
                Assert.Contains(
                    wheelButtons,
                    button => ReferenceEquals(
                        button,
                        window.FindName("RegionCaptureButton")));
                Assert.Contains(
                    wheelButtons,
                    button => button.Tag is LauncherItem item &&
                              item.Name == "轮盘应用 16");
                Assert.DoesNotContain(
                    wheelButtons,
                    button => button.Tag is LauncherItem item &&
                              item.Name == "底栏应用");

                scroller.Measure(new Size(380, 420));
                scroller.Arrange(new Rect(0, 0, 380, 420));
                scroller.UpdateLayout();
                Assert.True(
                    scroller.ScrollableHeight > 0,
                    "The unified wheel must scroll when all entries do not fit. " +
                    $"CanvasHeight={canvas.Height}, " +
                    $"ExtentHeight={scroller.ExtentHeight}, " +
                    $"ViewportHeight={scroller.ViewportHeight}, " +
                    $"ActualHeight={scroller.ActualHeight}, " +
                    $"ActualWidth={scroller.ActualWidth}, " +
                    $"Visibility={scroller.Visibility}.");
                scroller.ScrollToVerticalOffset(140);
                scroller.UpdateLayout();
                Assert.True(scroller.VerticalOffset > 0);
                window.Hide();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task WheelScrollEasesThroughIntermediateFrames()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var scroller = CreateScrollableWheelWindow(out var window);
                var expectedOffset = Math.Min(
                    120,
                    scroller.ScrollableHeight);
                Assert.True(expectedOffset > 20);

                InvokeSmoothWheelScroll(window, delta: -120);
                if (!SystemParameters.ClientAreaAnimation)
                {
                    Assert.Equal(expectedOffset, scroller.VerticalOffset, 1);
                    return;
                }

                Assert.Equal(0, scroller.VerticalOffset, 1);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(75));
                scroller.UpdateLayout();
                Assert.InRange(
                    scroller.VerticalOffset,
                    1,
                    expectedOffset - 1);

                PumpDispatcherFor(TimeSpan.FromMilliseconds(350));
                scroller.UpdateLayout();
                Assert.Equal(
                    expectedOffset,
                    scroller.VerticalOffset,
                    1);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task RapidWheelInputsConvergeOnTheLatestCombinedTarget()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var scroller = CreateScrollableWheelWindow(out var window);
                var expectedOffset = Math.Min(
                    240,
                    scroller.ScrollableHeight);

                InvokeSmoothWheelScroll(window, delta: -120);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(45));
                InvokeSmoothWheelScroll(window, delta: -120);
                PumpDispatcherFor(TimeSpan.FromMilliseconds(400));
                scroller.UpdateLayout();

                Assert.Equal(
                    expectedOffset,
                    scroller.VerticalOffset,
                    1);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Theory(Timeout = 10_000)]
    [InlineData(WheelActionKind.RegionCapture, false)]
    [InlineData(WheelActionKind.WindowCapture, false)]
    [InlineData(WheelActionKind.MonitorCapture, false)]
    [InlineData(WheelActionKind.AiBrowser, false)]
    [InlineData(WheelActionKind.Notes, false)]
    [InlineData(WheelActionKind.InputLock, false)]
    [InlineData(WheelActionKind.Settings, false)]
    public async Task DisabledBuiltInWheelActionIsHidden(
        WheelActionKind action,
        bool enabled)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var settings = AppSettings.CreateDefault();
                var enabledActions = settings.WheelActions
                    .Where(item => item != action)
                    .ToArray();
                var window = new QuickPanelWindow();
                window.Apply(
                    settings with { WheelActions = enabledActions },
                    manualLockEnabled: enabled,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(window, new Point(37, 280), opensLeft: false);

                var canvas = Assert.IsType<Canvas>(
                    window.FindName("WheelCanvas"));
                var visibleActions = canvas.Children
                    .OfType<Button>()
                    .Where(button => button.Visibility == Visibility.Visible)
                    .ToArray();
                Assert.Equal(6, visibleActions.Length);
                Assert.DoesNotContain(
                    visibleActions,
                    button => button.Name == action switch
                    {
                        WheelActionKind.RegionCapture => "RegionCaptureButton",
                        WheelActionKind.WindowCapture => "WindowCaptureButton",
                        WheelActionKind.MonitorCapture => "MonitorCaptureButton",
                        WheelActionKind.AiBrowser => "AiButton",
                        WheelActionKind.Notes => "NotesButton",
                        WheelActionKind.InputLock => "InputLockButton",
                        WheelActionKind.Settings => "SettingsButton",
                        _ => throw new ArgumentOutOfRangeException(nameof(action))
                    });
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task EmptyUnifiedWheelCollapsesWithoutCrashing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault() with { WheelActions = [] },
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(window, new Point(37, 280), opensLeft: false);

                var scroller = Assert.IsType<ScrollViewer>(
                    window.FindName("WheelScroller"));
                var fan = Assert.IsType<System.Windows.Shapes.Path>(
                    window.FindName("FanWash"));
                var geometry = Assert.IsAssignableFrom<Geometry>(fan.Data);
                Assert.Equal(Visibility.Collapsed, scroller.Visibility);
                Assert.True(geometry.Bounds.IsEmpty);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task OpeningWheelAddsOutwardPerItemTransition()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                InvokeArrangeMenu(window, new Point(37, 280), opensLeft: false);
                InvokePlayEntranceAnimation(window);

                var button = Assert.IsType<Button>(
                    window.FindName("RegionCaptureButton"));
                var transforms = Assert.IsType<TransformGroup>(
                    button.RenderTransform);

                Assert.Contains(
                    transforms.Children,
                    transform => transform is TranslateTransform);
                Assert.Contains(
                    transforms.Children,
                    transform => transform is ScaleTransform);
                Assert.Equal(
                    new Point(0.5, 0.5),
                    button.RenderTransformOrigin);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task AnimatedCloseFinishesWithoutLeavingThePanelVisible()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                window.Apply(
                    AppSettings.CreateDefault(),
                    manualLockEnabled: false,
                    inputStatus: "等待前台应用");
                window.Show();
                window.UpdateLayout();
                Assert.True(window.IsVisible);

                InvokeAnimatedHide(window);
                PumpDispatcherUntil(() => !window.IsVisible);

                Assert.False(
                    window.IsVisible,
                    "The quick panel must leave hit testing after its " +
                    "closing transition completes.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void InvokeArrangeMenu(
        QuickPanelWindow window,
        Point anchor,
        bool opensLeft)
    {
        var method = typeof(QuickPanelWindow).GetMethod(
            "ArrangeMenu",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ArrangeMenu was not found.");
        method.Invoke(window, [anchor, opensLeft]);
    }

    private static void InvokePlayEntranceAnimation(
        QuickPanelWindow window)
    {
        var method = typeof(QuickPanelWindow).GetMethod(
            "PlayEntranceAnimation",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "PlayEntranceAnimation was not found.");
        method.Invoke(window, null);
    }

    private static void InvokeAnimatedHide(QuickPanelWindow window)
    {
        var method = typeof(QuickPanelWindow).GetMethod(
            "HideAnimated",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "HideAnimated was not found.");
        method.Invoke(window, null);
    }

    private static void InvokeSmoothWheelScroll(
        QuickPanelWindow window,
        int delta)
    {
        var method = typeof(QuickPanelWindow).GetMethod(
            "BeginSmoothWheelScroll",
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "BeginSmoothWheelScroll was not found.");
        method.Invoke(window, [delta]);
    }

    private static ScrollViewer CreateScrollableWheelWindow(
        out QuickPanelWindow window)
    {
        window = new QuickPanelWindow();
        var wheelApps = Enumerable.Range(0, 16)
            .Select(index => new LauncherItem(
                Guid.NewGuid(),
                $"滚动应用 {index + 1}",
                LauncherKind.Exe,
                $@"C:\Apps\scroll{index}.exe",
                [],
                null,
                index,
                ShowInWheel: true))
            .ToArray();
        window.Apply(
            AppSettings.CreateDefault() with
            {
                Launchers = wheelApps
            },
            manualLockEnabled: false,
            inputStatus: "等待前台应用");
        InvokeArrangeMenu(window, new Point(37, 280), opensLeft: false);
        window.Show();
        window.Measure(new Size(660, 560));
        window.Arrange(new Rect(0, 0, 660, 560));
        window.UpdateLayout();

        var scroller = Assert.IsType<ScrollViewer>(
            window.FindName("WheelScroller"));
        scroller.ApplyTemplate();
        scroller.Measure(new Size(380, 420));
        scroller.Arrange(new Rect(0, 0, 380, 420));
        scroller.UpdateLayout();
        Assert.True(
            scroller.ScrollableHeight > 120,
            "The test wheel must have enough overflow for smooth scrolling.");
        return scroller;
    }

    private static void PumpDispatcherFor(TimeSpan duration)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static void PumpDispatcherUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static Rect SurfaceBounds(
        FrameworkElement element,
        double inset)
    {
        var width = element.Width;
        var height = element.Height;
        if (double.IsNaN(width))
            width = element.ActualWidth;
        if (double.IsNaN(height))
            height = element.ActualHeight;
        var left = Canvas.GetLeft(element);
        var top = Canvas.GetTop(element);
        Assert.False(double.IsNaN(left));
        Assert.False(double.IsNaN(top));
        return new Rect(
            left + inset,
            top + inset,
            Math.Max(1, width - (inset * 2)),
            Math.Max(1, height - (inset * 2)));
    }

    private static Rect WheelSurfaceBounds(
        QuickPanelWindow window,
        FrameworkElement element,
        double inset)
    {
        var scroller = Assert.IsType<ScrollViewer>(
            window.FindName("WheelScroller"));
        var left = Canvas.GetLeft(scroller) +
                   Canvas.GetLeft(element) -
                   scroller.HorizontalOffset;
        var top = Canvas.GetTop(scroller) +
                  Canvas.GetTop(element) -
                  scroller.VerticalOffset;
        return new Rect(
            left + inset,
            top + inset,
            Math.Max(1, element.Width - (inset * 2)),
            Math.Max(1, element.Height - (inset * 2)));
    }

    private static void AssertNoKeyboardFocusTrigger(
        FrameworkElement owner,
        string styleKey)
    {
        var style = Assert.IsType<Style>(owner.FindResource(styleKey));
        var templateSetter = Assert.Single(
            style.Setters.OfType<Setter>(),
            setter => setter.Property == Control.TemplateProperty);
        var template = Assert.IsType<ControlTemplate>(templateSetter.Value);
        Assert.DoesNotContain(
            template.Triggers.OfType<Trigger>(),
            trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty);
    }
}
