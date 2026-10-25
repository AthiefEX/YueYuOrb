using System.Windows;
using System.Windows.Controls;
using FloatDesk.App.Views;

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