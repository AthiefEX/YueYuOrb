using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatDesk.App.Views;
using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class DanmakuOverlayTests
{
    [Fact]
    public void OverlayExpiresVerticalRowsAndWheelUsesExistingStyle()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var overlay = new DanmakuOverlayWindow(); var panel = new QuickPanelWindow();
            try
            {
                overlay.ApplySettings(new() { Vertical = true, HeightDip = 80, DurationSeconds = 3 });
                Assert.False(overlay.ShowActivated);
                Assert.True(overlay.CanPresent);
                overlay.Present(new("1", "TIM", "群", "第一条"), false);
                overlay.Present(new("2", "TIM", "群", "第二条"), true);
                Assert.False(overlay.CanPresent);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3400) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start(); Dispatcher.PushFrame(frame);
                Assert.True(overlay.CanPresent); Assert.False(overlay.IsVisible);
                var settings = AppSettings.CreateDefault() with { Danmaku = new() { ShowInWheel = true, Enabled = true } };
                panel.Apply(settings, false, "");
                var button = ((Canvas)panel.FindName("WheelCanvas")).Children.OfType<Button>().Single(b => AutomationProperties.GetName(b).StartsWith("消息弹幕", StringComparison.Ordinal));
                Assert.Same(panel.FindResource("RadialActionButtonStyle"), button.Style);
                var invoked = false; panel.DanmakuRequested += () => invoked = true;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.True(invoked);
                panel.Apply(settings with { Danmaku = new() { ShowInWheel = false } }, false, "");
                Assert.DoesNotContain(((Canvas)panel.FindName("WheelCanvas")).Children.OfType<Button>(), b => AutomationProperties.GetName(b).StartsWith("消息弹幕", StringComparison.Ordinal));
            }
            catch (Exception e) { error = e; }
            finally { overlay.Clear(); overlay.Close(); panel.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(12)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
