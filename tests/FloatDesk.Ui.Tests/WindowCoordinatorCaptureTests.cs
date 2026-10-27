using System.Windows;
using System.Windows.Threading;
using FloatDesk.App.Coordination;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class WindowCoordinatorCaptureTests
{
    [Fact]
    public void CaptureHidingRestoresRegisteredTransientWindow()
    {
        RunScenario(async () =>
        {
            var orb = new Window();
            var quickPanel = new Window();
            var transient = new TranslationOverlayWindow();
            try
            {
                orb.Show();
                quickPanel.Show();
                var operation = transient.BeginOperation();
                transient.ShowStatus(
                    operation,
                    "字幕翻译准备中。",
                    new PixelPoint(40, 40));
                var coordinator = new WindowCoordinator(
                    [],
                    orb,
                    quickPanel);
                coordinator.RegisterCaptureHiddenWindow(transient);

                var visibility = await coordinator.HideForCaptureAsync(
                    CancellationToken.None);
                Assert.False(transient.IsVisible);

                await visibility.DisposeAsync();
                Assert.True(transient.IsVisible);

                coordinator.UnregisterCaptureHiddenWindow(transient);
            }
            finally
            {
                transient.Close();
                quickPanel.Close();
                orb.Close();
            }
        });
    }

    [Fact]
    public void DismissedTransientWindowIsNotResurrectedAfterCapture()
    {
        RunScenario(async () =>
        {
            var orb = new Window();
            var quickPanel = new Window();
            var transient = new TranslationOverlayWindow();
            try
            {
                orb.Show();
                quickPanel.Show();
                var operation = transient.BeginOperation();
                transient.ShowStatus(
                    operation,
                    "字幕翻译准备中。",
                    new PixelPoint(40, 40));
                var coordinator = new WindowCoordinator(
                    [],
                    orb,
                    quickPanel);
                coordinator.RegisterCaptureHiddenWindow(transient);

                var visibility = await coordinator.HideForCaptureAsync(
                    CancellationToken.None);
                transient.DismissCurrentOperation();
                await visibility.DisposeAsync();

                Assert.False(transient.IsVisible);
                Assert.False(transient.CanRestoreAfterCapture);
            }
            finally
            {
                transient.Close();
                quickPanel.Close();
                orb.Close();
            }
        });
    }

    [Fact]
    public void SubtitleOverlayRestoresClickThroughAfterCapture()
    {
        RunScenario(async () =>
        {
            var orb = new Window();
            var quickPanel = new Window();
            var subtitle = new TranslationOverlayWindow();
            try
            {
                orb.Show();
                quickPanel.Show();
                var operation = subtitle.BeginOperation(subtitle: true);
                subtitle.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                subtitle.ShowSubtitleText(operation, "字幕");
                var coordinator = new WindowCoordinator(
                    [],
                    orb,
                    quickPanel);
                coordinator.RegisterCaptureHiddenWindow(subtitle);

                var visibility = await coordinator.HideForCaptureAsync(
                    CancellationToken.None);
                Assert.False(subtitle.IsVisible);

                await visibility.DisposeAsync();

                Assert.True(subtitle.IsVisible);
                Assert.True(subtitle.IsClickThrough);
                coordinator.UnregisterCaptureHiddenWindow(subtitle);
            }
            finally
            {
                subtitle.Close();
                quickPanel.Close();
                orb.Close();
            }
        });
    }

    [Fact]
    public void CancellationDuringCaptureHidingRestoresWindows()
    {
        RunScenario(async () =>
        {
            var orb = new Window();
            var quickPanel = new Window();
            var transient = new Window();
            using var cancellation = new CancellationTokenSource();
            try
            {
                orb.Show();
                quickPanel.Show();
                transient.Show();
                var coordinator = new WindowCoordinator(
                    [],
                    orb,
                    quickPanel);
                coordinator.RegisterCaptureHiddenWindow(transient);
                orb.IsVisibleChanged += (_, _) =>
                {
                    if (!orb.IsVisible)
                        cancellation.Cancel();
                };

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    coordinator.HideForCaptureAsync(cancellation.Token));

                Assert.True(orb.IsVisible);
                Assert.True(transient.IsVisible);
                coordinator.UnregisterCaptureHiddenWindow(transient);
            }
            finally
            {
                transient.Close();
                quickPanel.Close();
                orb.Close();
            }
        });
    }

    private static void RunScenario(Func<Task> scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await scenario();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(10)),
            "STA test thread timed out.");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
