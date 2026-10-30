using System.Windows;
using System.Windows.Interop;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class PopupInteractionTests
{
    [Fact]
    public async Task ShutdownClosesWindowsBeforeAsynchronouslyDrainingTheirSaves()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = false;
        var closed = false;
        var queue = new PopupPlacementSaveQueue(async _ => { await release.Task; saved = true; });
        var drain = queue.DrainAfterClosingAsync(() =>
        {
            closed = true;
            _ = queue.Enqueue(new("audio-panel", "screen", .5, .5, 300, 200));
        }, default);
        Assert.True(closed);
        Assert.False(drain.IsCompleted);
        release.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(saved);
    }
    [Fact]
    public async Task PositionSavesStayOrderedWhenFirstWriteIsSlow()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<double>();
        var queue = new PopupPlacementSaveQueue(async placement =>
        {
            if (placement.RelativeX == .1) { firstStarted.TrySetResult(); await release.Task; }
            order.Add(placement.RelativeX);
        });
        var first = queue.Enqueue(new("audio-panel", "screen", .1, .2, 300, 200));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = queue.Enqueue(new("audio-panel", "screen", .8, .2, 300, 200));
        Assert.False(second.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal([.1, .8], order);
    }
    [Fact]
    public void OnlyOutsideClicksDismissAndSavedPositionRestores()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var window = new Window { Width = 300, Height = 180, Left = 100, Top = 100 };
            try
            {
                window.Show();
                var textBox = new System.Windows.Controls.TextBox { Text = "可复制文字" };
                window.Content = textBox;
                var dismissed = 0;
                using var behavior = new OutsideClickDismissBehavior(window, () => dismissed++, () => true, installHook: false);
                using var nativeMonitor = new OutsideClickDismissBehavior(window, () => dismissed++, () => false);
                Assert.True(nativeMonitor.IsMonitoring);
                var bounds = FloatDesk.Windows.Capture.DesktopGeometry.GetVisibleWindowBounds(new WindowInteropHelper(window).Handle);
                behavior.ProcessPointerDown(bounds.Left + 20, bounds.Top + 30);
                Assert.Equal(0, dismissed);
                var contextMenu = new System.Windows.Controls.ContextMenu { PlacementTarget = textBox };
                contextMenu.Items.Add(new System.Windows.Controls.MenuItem { Header = "复制" });
                contextMenu.IsOpen = true;
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var menuPoint = contextMenu.PointToScreen(new Point(5, 5));
                behavior.ProcessPointerDown((int)menuPoint.X, (int)menuPoint.Y);
                contextMenu.IsOpen = false;
                Assert.Equal(0, dismissed);
                behavior.ProcessPointerDown(bounds.Right + 30, bounds.Bottom + 30);
                Assert.Equal(1, dismissed);
                var placement = PopupWindowInteraction.CapturePlacement(window, "audio-panel");
                window.Left += 100;
                Assert.True(PopupWindowInteraction.TryRestore(window, placement));
                var restored = FloatDesk.Windows.Capture.DesktopGeometry.GetVisibleWindowBounds(new WindowInteropHelper(window).Handle);
                Assert.InRange(Math.Abs(restored.Left - bounds.Left), 0, 2);
                behavior.QueuePointerDown(-10000, -10000);
                window.Hide();
                Assert.False(nativeMonitor.IsMonitoring);
                window.Show();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, dismissed);
                window.Hide();
                behavior.ProcessPointerDown(-10000, -10000);
                Assert.Equal(1, dismissed);
            }
            catch (Exception ex) { error = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
