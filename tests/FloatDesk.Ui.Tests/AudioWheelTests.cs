using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class AudioWheelTests
{
    [Fact]
    public void AudioEntriesRespectVisibilityAndDispatchGroupWithoutLaunchingAnApp()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var panel = new QuickPanelWindow();
            try
            {
                var group = new AudioCycleGroup(Guid.NewGuid(), "耳机与音箱", ["a", "b"]);
                var defaults = (AppSettings.CreateDefault() with { Brightness = new(ShowBrightnessInWheel: false, ShowNightModeInWheel: false) });
                panel.Apply(defaults with { AudioGroups = [group], WheelActions = [] }, false, "");
                Assert.Equal(Visibility.Visible, Assert.IsType<ScrollViewer>(panel.FindName("WheelScroller")).Visibility);
                var visible = (Button[])panel.GetType().GetMethod("GetVisibleWheelButtons", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null)!;
                Assert.Equal(2, visible.Length);
                var canvas = Assert.IsType<Canvas>(panel.FindName("WheelCanvas"));
                var devices = Assert.Single(canvas.Children.OfType<Button>(), b => AutomationProperties.GetName(b) == "播放设备");
                var groupButton = Assert.Single(canvas.Children.OfType<Button>(), b => AutomationProperties.GetName(b) == group.Name);
                AudioCycleGroup? requested = null;
                var opened = false;
                panel.AudioRequested += () => opened = true;
                panel.AudioGroupRequested += value => requested = value;
                devices.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                groupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(opened);
                Assert.Equal(group, requested);
                panel.Apply(defaults with { ShowAudioInWheel = false, AudioGroups = [group with { ShowInWheel = false }] }, false, "");
                Assert.DoesNotContain(canvas.Children.OfType<Button>(), b => AutomationProperties.GetName(b) is "播放设备" or "耳机与音箱");
            }
            catch (Exception ex) { error = ex; }
            finally { panel.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
