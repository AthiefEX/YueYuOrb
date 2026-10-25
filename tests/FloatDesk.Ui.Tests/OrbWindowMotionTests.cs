using System.Reflection;
using System.Windows.Media;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class OrbWindowMotionTests
{
    [Fact(Timeout = 10_000)]
    public async Task OrbHasDedicatedDragScaleVisual()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateWindow();
                window.ApplySettings(AppSettings.CreateDefault());

                var scale = Assert.IsType<ScaleTransform>(
                    window.FindName("OrbDragScale"));
                Assert.Equal(1, scale.ScaleX);
                Assert.Equal(1, scale.ScaleY);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact(Timeout = 10_000)]
    public async Task StartingDragStartsScaleFeedback()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = CreateWindow();
                window.ApplySettings(AppSettings.CreateDefault());
                var method = window.GetType().GetMethod(
                    "BeginDragVisuals",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "BeginDragVisuals was not found.");
                method.Invoke(window, null);

                var scale = Assert.IsType<ScaleTransform>(
                    window.FindName("OrbDragScale"));
                Assert.True(
                    scale.HasAnimatedProperties,
                    "Dragging must animate the dedicated orb scale instead " +
                    "of resizing or repeatedly laying out the window.");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static OrbWindow CreateWindow() =>
        new(
            () => { },
            _ => { },
            () => { },
            () => { },
            () => { },
            () => { },
            () => { },
            () => { });
}
