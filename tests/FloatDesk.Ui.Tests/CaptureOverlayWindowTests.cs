using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class CaptureOverlayWindowTests
{
    [Fact(Timeout = 10_000)]
    public async Task ClosingOverlayRequestsCancellation()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var bounds = new PixelRect(0, 0, 16, 16);
                var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
                var bitmap = BitmapSource.Create(
                    16,
                    16,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    16 * 4);
                var monitor = new DisplayMonitor(
                    @"\\.\DISPLAY1",
                    bounds,
                    bounds,
                    true);
                var window = new CaptureOverlayWindow(monitor, bounds, bitmap);
                var cancellationRequested = false;
                window.CancelRequested += () => cancellationRequested = true;
                window.Show();

                window.Close();

                Assert.True(cancellationRequested);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));
    }

    [Fact(Timeout = 10_000)]
    public async Task CoordinatorCompletionClosesWithoutRequestingCancellation()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var bounds = new PixelRect(0, 0, 16, 16);
                var pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
                var bitmap = BitmapSource.Create(
                    16,
                    16,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    16 * 4);
                var monitor = new DisplayMonitor(
                    @"\\.\DISPLAY1",
                    bounds,
                    bounds,
                    true);
                var window = new CaptureOverlayWindow(monitor, bounds, bitmap);
                var cancellationRequested = false;
                window.CancelRequested += () => cancellationRequested = true;
                window.Show();

                window.CloseWithoutCancelling();

                Assert.False(cancellationRequested);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
