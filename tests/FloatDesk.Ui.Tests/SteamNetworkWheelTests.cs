using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class SteamNetworkWheelTests
{
    [Fact]
    public void NetworkWheelUsesExistingStyleDispatchesAndDoesNotDuplicate()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var panel = new QuickPanelWindow();
            try
            {
                var settings = AppSettings.CreateDefault();
                var canvas = (Canvas)panel.FindName("WheelCanvas");
                panel.Apply(settings, false, "");
                Assert.DoesNotContain(canvas.Children.OfType<Button>(), button => AutomationProperties.GetName(button) == "Steam 网络加速");
                settings = settings with { SteamNetwork = new() { ShowInWheel = true } };
                panel.Apply(settings, false, "");
                panel.Apply(settings, false, "");
                var button = Assert.Single(canvas.Children.OfType<Button>(), button => AutomationProperties.GetName(button) == "Steam 网络加速");
                Assert.Same(panel.FindResource("RadialActionButtonStyle"), button.Style);
                var count = 0;
                panel.NetworkRequested += () => count++;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, count);
            }
            catch (Exception exception) { error = exception; }
            finally { panel.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
