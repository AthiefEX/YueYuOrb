using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    public async Task WheelAppsUseScrollableRailAndOnlyDockItemsOpenDock()
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

                var scroller = Assert.IsType<ScrollViewer>(
                    window.FindName("WheelLauncherScroller"));
                var panel = Assert.IsType<StackPanel>(
                    window.FindName("WheelLauncherPanel"));
                var dock = Assert.IsType<Border>(
                    window.FindName("LauncherDock"));

                Assert.Equal(Visibility.Visible, scroller.Visibility);
                Assert.Equal(16, panel.Children.Count);
                Assert.Equal(Visibility.Visible, dock.Visibility);

                scroller.Measure(new Size(234, 380));
                scroller.Arrange(new Rect(0, 0, 234, 380));
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
                    window.FindName("WheelLauncherScroller"));
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
                    var bounds = SurfaceBounds(element, inset: 4);
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
                    var bounds = SurfaceBounds(button, inset: 4);
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
