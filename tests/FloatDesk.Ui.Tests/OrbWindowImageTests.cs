using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class OrbWindowImageTests
{
    [Fact(Timeout = 10_000)]
    public async Task CustomImageHidesFallbackCircleAndText()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FloatDeskTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "orb.png");
        WriteTransparentPng(path);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new OrbWindow(
                    () => { },
                    _ => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    path);
                window.ApplySettings(AppSettings.CreateDefault());

                Assert.Equal(
                    Visibility.Visible,
                    GetField<Image>(window, "OrbImage").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    GetField<FrameworkElement>(window, "OrbCircle").Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    GetField<FrameworkElement>(window, "FallbackText").Visibility);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "STA test thread timed out.");
        await Task.CompletedTask;
        try
        {
            if (failure is not null)
                throw new Xunit.Sdk.XunitException(failure.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task OrbGlowHasTransparentRoomAndIsNotRootClipped()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new OrbWindow(
                    () => { },
                    _ => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { },
                    () => { });
                window.ApplySettings(AppSettings.CreateDefault());

                var layout = Assert.IsType<StackPanel>(
                    window.FindName("OrbLayout"));
                var root = Assert.IsType<Grid>(
                    window.FindName("OrbRoot"));
                var image = Assert.IsType<Image>(
                    window.FindName("OrbImage"));

                Assert.Null(root.Clip);
                Assert.IsType<EllipseGeometry>(image.Clip);
                Assert.True(
                    layout.Margin.Left >= 20 &&
                    layout.Margin.Top >= 20 &&
                    layout.Margin.Right >= 20 &&
                    layout.Margin.Bottom >= 20,
                    "The per-pixel transparent window must leave room " +
                    "for the orb shadow and glow.");
                Assert.Equal(
                    AppSettings.CreateDefault().Orb.SizeDip + 40,
                    window.Width);
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

    private static void WriteTransparentPng(string path)
    {
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[16],
            8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Field {name} was not found."));
}
