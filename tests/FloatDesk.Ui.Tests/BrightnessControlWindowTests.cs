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
    public void PopupIsCoveredByWindowEventsWithoutWaitingForPolling()
    {
        RunSta(() =>
        {
            using var night = new NightModeController(Dispatcher.CurrentDispatcher);
            using var popup = new System.Windows.Forms.Form { TopMost = true, ShowInTaskbar = false };
            night.SetDimmingPercent(0);
            night.SetEnabled(true);
            // Disable polling entirely: covering this new popup must be event driven.
            ((DispatcherTimer)typeof(NightModeController)
                .GetField("_stackTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(night)!).Stop();
            popup.Show();
            var foreground = GetForegroundWindow();
            Pump(75);
            var windows = (List<NightDimWindow>)typeof(NightModeController)
                .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(night)!;
            var mask = windows.Single(w => System.Windows.Forms.Screen.FromHandle(
                new System.Windows.Interop.WindowInteropHelper(w).Handle).DeviceName ==
                System.Windows.Forms.Screen.FromHandle(popup.Handle).DeviceName);
            var handle = new System.Windows.Interop.WindowInteropHelper(mask).Handle;
            Assert.True(IsAbove(handle, popup.Handle));
            Assert.Equal(foreground, GetForegroundWindow());
            popup.Hide();
            popup.Show();
            Pump(75);
            Assert.True(IsAbove(handle, popup.Handle));
            using (night.Suspend())
            {
                popup.Hide(); popup.Show(); Pump(75);
                Assert.All(windows, w => Assert.False(w.IsVisible));
            }
        });
    }

    [Fact]
    public void DimmingChangesAreCoalescedAndClosingFlushesLastValue()
    {
        RunSta(() =>
        {
            using var night = new NightModeController(Dispatcher.CurrentDispatcher);
            using var window = new BrightnessControlWindow(new FakeBrightness(), night, new());
            var changed = typeof(BrightnessControlWindow).GetEvent("DimmingCommitted");
            Assert.NotNull(changed);
            var saved = new List<int>();
            var previews = new List<int>();
            window.DimmingPreviewChanged += previews.Add;
            changed.AddEventHandler(window, (Action<int>)saved.Add);
            window.Show(); Pump(70);
            Assert.Empty(saved);
            var slider = FindDimmingSlider(window);
            slider.Value = 50; slider.Value = 61; slider.Value = 72;
            Assert.Equal(3, previews.Count);
            Assert.Equal(72, previews[^1]);
            Assert.Empty(saved);
            Pump(350);
            Assert.Equal(72, Assert.Single(saved));
            slider.Value = 83;
            window.Close();
            Assert.Equal(2, saved.Count);
            Assert.Equal(83, saved[1]);
            Pump(350);
            Assert.Equal(2, saved.Count);
        });
    }

    [Fact]
    public void LaterTopmostPopupIsCoveredWithoutTakingFocusAndSuspensionHidesMask()
    {
        RunSta(() =>
        {
            using var night = new NightModeController(Dispatcher.CurrentDispatcher);
            using var popup = new System.Windows.Forms.Form { TopMost = true, ShowInTaskbar = false };
            night.SetDimmingPercent(0); // Exercise native stacking without darkening the test desktop.
            night.SetEnabled(true);
            popup.Show();
            var foreground = GetForegroundWindow();
            Pump(400);
            var windows = (List<NightDimWindow>)typeof(NightModeController)
                .GetField("_windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(night)!;
            var mask = windows.Single(w => System.Windows.Forms.Screen.FromHandle(
                new System.Windows.Interop.WindowInteropHelper(w).Handle).DeviceName ==
                System.Windows.Forms.Screen.FromHandle(popup.Handle).DeviceName);
            var maskHandle = new System.Windows.Interop.WindowInteropHelper(mask).Handle;
            Assert.True(IsAbove(maskHandle, popup.Handle));
            Assert.Equal(foreground, GetForegroundWindow());
            using (night.Suspend())
            {
                Pump(200);
                Assert.All(windows, w => Assert.False(w.IsVisible));
            }
            Pump(200);
            Assert.True(mask.IsVisible);
            Assert.True(IsAbove(maskHandle, popup.Handle));
            night.SetEnabled(false);
            Pump(200);
            Assert.All(windows, w => Assert.False(w.IsVisible));
        });
    }

    private static Slider FindDimmingSlider(System.Windows.DependencyObject node)
    {
        if (node is Slider slider && System.Windows.Automation.AutomationProperties.GetName(slider) == "黑夜降暗程度") return slider;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindDimmingSliderOrNull(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Dimming slider not found.");
    }
    private static Slider? FindDimmingSliderOrNull(System.Windows.DependencyObject node)
    {
        if (node is Slider slider && System.Windows.Automation.AutomationProperties.GetName(slider) == "黑夜降暗程度") return slider;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var found = FindDimmingSliderOrNull(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            if (found is not null) return found;
        }
        return null;
    }
    private static bool IsAbove(nint upper, nint lower)
    {
        for (var handle = GetWindow(lower, 3); handle != 0; handle = GetWindow(handle, 3))
            if (handle == upper) return true;
        return false;
    }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

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
