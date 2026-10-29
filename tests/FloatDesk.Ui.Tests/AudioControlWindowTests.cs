using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class AudioControlWindowTests
{
    [Fact]
    public void PendingDeviceReadDisablesControlsAndReenablesAfterArrival()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var audio = new FakeAudio { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var window = new AudioControlWindow(audio);
            try
            {
                window.Show(); Pump(30);
                Assert.False(Field<Slider>(window, "_volume").IsEnabled);
                Assert.False(Field<Button>(window, "_mute").IsEnabled);
                Assert.False(Field<StackPanel>(window, "_devices").IsEnabled);
                audio.Pending.SetResult(new AudioSnapshot([new("a", "音箱")], "a", 0.7, false));
                Pump(100);
                Assert.True(Field<Slider>(window, "_volume").IsEnabled);
                Assert.True(Field<Button>(window, "_mute").IsEnabled);
                Assert.True(Field<StackPanel>(window, "_devices").IsEnabled);
            }
            catch (Exception ex) { error = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void DeviceVolumeAndMuteControlsUseDisplayedEndpoint()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var audio = new FakeAudio();
            var window = new AudioControlWindow(audio);
            try
            {
                window.Show(); Pump(50);
                var devices = Field<StackPanel>(window, "_devices");
                Assert.Equal(2, devices.Children.Count);
                Assert.Contains("✓", Assert.IsType<TextBlock>(((Button)devices.Children[0]).Content).Text);
                ((Button)devices.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("b", audio.Current);
                Field<Slider>(window, "_volume").Value = 35;
                Pump(300);
                Assert.Equal("b", audio.VolumeDevice);
                Assert.Equal(0.35, audio.Volume, 2);
                Field<Button>(window, "_mute").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(audio.Muted);
                Assert.Equal("b", audio.MuteDevice);
            }
            catch (Exception ex) { error = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class FakeAudio : IAudioService
    {
        public TaskCompletionSource<AudioSnapshot>? Pending;
        public string Current = "a";
        public string? VolumeDevice;
        public string? MuteDevice;
        public double Volume = 0.7;
        public bool Muted;
        public Task<AudioSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Pending?.Task ?? Task.FromResult(new AudioSnapshot([new("a", "音箱"), new("b", "耳机")], Current, Volume, Muted));
        public Task SetDefaultDeviceAsync(string deviceId, CancellationToken cancellationToken) { Current = deviceId; return Task.CompletedTask; }
        public Task SetVolumeAsync(string deviceId, double volume, CancellationToken cancellationToken) { VolumeDevice = deviceId; Volume = volume; return Task.CompletedTask; }
        public Task SetMuteAsync(string deviceId, bool muted, CancellationToken cancellationToken) { MuteDevice = deviceId; Muted = muted; return Task.CompletedTask; }
    }
}
