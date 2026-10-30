using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatDesk.App.Brightness;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class BrightnessControlWindowTests
{
    [Fact]
    public void OpeningDoesNotWriteAndRapidDraggingUsesLastValueOnAllScreens()
    {
        RunSta(() =>
        {
            using var night = new NightModeController(Dispatcher.CurrentDispatcher);
            var service = new FakeBrightness();
            using var window = new BrightnessControlWindow(service, night, new());
            window.Show(); Pump(70);
            Assert.Empty(service.Writes);
            var slider = (Slider)typeof(BrightnessControlWindow).GetField("_brightness", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            slider.Value = 20; slider.Value = 35; slider.Value = 42;
            Pump(300);
            Assert.Equal(2, service.Writes.Count);
            Assert.All(service.Writes, write => Assert.Equal(42, write.Item2));
        });
    }

    [Fact]
    public void TransparentOverlayCoversPhysicalBoundsAndDoesNotActivate()
    {
        RunSta(() =>
        {
            var window = new NightDimWindow { Opacity = 0 };
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                window.SetPhysicalBounds(bounds);
                window.Show(); Pump(70);
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Assert.True(GetWindowRect(handle, out var rect));
                Assert.Equal(bounds.Width, rect.Right - rect.Left);
                Assert.Equal(bounds.Height, rect.Bottom - rect.Top);
                Assert.False(window.ShowActivated);
                Assert.False(window.IsHitTestVisible);
                Assert.False(window.ShowInTaskbar);
            }
            finally { window.Close(); }
        });
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out Rect rect);
    private sealed class FakeBrightness : IBrightnessService
    {
        public List<(string, int)> Writes = [];
        public Task<BrightnessSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new BrightnessSnapshot([new("a", "A", 10, null, true), new("b", "B", 80, null, false)]));
        public Task<BrightnessWriteResult> SetPercentAsync(string deviceId, int percent, CancellationToken cancellationToken)
        { Writes.Add((deviceId, percent)); return Task.FromResult(new BrightnessWriteResult(deviceId, percent, null)); }
        public void Dispose() { }
    }
}
