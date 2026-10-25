using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class QuickPanelWindowLayoutTests
{
    [Fact(Timeout = 10_000)]
    public async Task ActionsUseUniformGeometryWithoutSelectionBorder()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                string[] actionNames =
                [
                    "RegionCaptureButton",
                    "WindowCaptureButton",
                    "MonitorCaptureButton",
                    "AiButton",
                    "NotesButton",
                    "InputLockButton",
                    "SettingsButton"
                ];

                foreach (var name in actionNames)
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
    public async Task WheelMenuUsesSolidBackgroundGuides()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new QuickPanelWindow();
                AssertSolidLine(window.FindName("ConnectorPath"));
                AssertSolidLine(window.FindName("AnchorOrbitSmall"));
                AssertSolidLine(window.FindName("AnchorOrbitLarge"));
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

    private static void AssertSolidLine(object? element)
    {
        var shape = Assert.IsAssignableFrom<Shape>(element);
        Assert.True(
            shape.StrokeDashArray is null ||
            shape.StrokeDashArray.Count == 0,
            "Wheel guide lines must be solid.");
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