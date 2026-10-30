using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class BrightnessWheelTests
{
    [Fact]
    public void ButtonsDispatchUseExistingStyleAndRespectVisibility()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var panel = new QuickPanelWindow();
            try
            {
                var settings = AppSettings.CreateDefault() with { WheelActions = [], ShowAudioInWheel = false };
                panel.Apply(settings, false, "");
                var buttons = ((Canvas)panel.FindName("WheelCanvas")).Children.OfType<Button>().Where(b => b.Tag?.GetType().Name == "AudioWheelTag").ToArray();
                Assert.Equal(2, buttons.Length);
                var opened = false; var toggled = false;
                panel.BrightnessRequested += () => opened = true;
                panel.NightModeRequested += () => toggled = true;
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(opened); Assert.True(toggled);
                Assert.All(buttons, button => { Assert.Equal(196, button.Width); Assert.Equal(48, button.Height); Assert.Same(panel.FindResource("RadialActionButtonStyle"), button.Style); });
                panel.SetNightModeEnabled(true);
                Assert.Contains("已开启", AutomationProperties.GetName(buttons[1]));
                panel.Apply(settings with { Brightness = new(ShowBrightnessInWheel: false, ShowNightModeInWheel: false) }, false, "");
                Assert.DoesNotContain(((Canvas)panel.FindName("WheelCanvas")).Children.OfType<Button>(), b => AutomationProperties.GetName(b).StartsWith("亮度调节", StringComparison.Ordinal) || AutomationProperties.GetName(b).StartsWith("黑夜模式", StringComparison.Ordinal));
            }
            catch (Exception ex) { error = ex; }
            finally { panel.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
