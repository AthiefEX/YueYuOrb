using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class CrystalOrbWindowTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(56)]
    [InlineData(80)]
    [InlineData(128)]
    public Task CrystalImageAndShellScaleTogetherWithoutGrowingWindow(int diameter) => RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            window.ApplySettings(Settings(diameter));
            var root = Find<Grid>(window, "OrbRoot");
            var image = Find<Image>(window, "OrbImage");
            var shell = Find<CrystalOrbSurface>(window, "CrystalSurface");
            var clip = Assert.IsType<EllipseGeometry>(image.Clip);

            Assert.Equal(diameter, root.Width);
            Assert.Equal(diameter + 40, window.Width);
            Assert.Equal(new Thickness(diameter * 0.035), image.Margin);
            Assert.Equal(diameter * 0.93 / 2, clip.RadiusX, 6);
            Assert.Equal(Visibility.Visible, shell.Visibility);
            Assert.IsType<EllipseGeometry>(root.Clip);
            Assert.Null(Find<FrameworkElement>(window, "OrbCircle").Effect);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SwitchingBackToClassicRestoresContourAndShadow() => RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            var crystal = Settings(56) with
            {
                Orb = Settings(56).Orb with { ImageShape = OrbImageShape.Contour }
            };
            window.ApplySettings(crystal);
            Assert.IsType<EllipseGeometry>(Find<Image>(window, "OrbImage").Clip);

            window.ApplySettings(crystal with
            {
                Orb = crystal.Orb with { DisplayMode = OrbDisplayMode.Classic }
            });

            Assert.Null(Find<Image>(window, "OrbImage").Clip);
            Assert.Equal(new Thickness(0), Find<Image>(window, "OrbImage").Margin);
            Assert.Null(Find<Grid>(window, "OrbRoot").Clip);
            Assert.Null(Find<Grid>(window, "OrbRoot").Background);
            Assert.NotNull(Find<FrameworkElement>(window, "OrbCircle").Effect);
            Assert.Equal(Visibility.Collapsed, Find<CrystalOrbSurface>(window, "CrystalSurface").Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CrystalKeepsClockAboveGlassWithoutBackdrop() => RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            var settings = Settings(56);
            window.ApplySettings(settings with { Orb = settings.Orb with { ShowClock = true } });
            var root = Find<Grid>(window, "OrbRoot");
            var clock = Find<Border>(window, "ClockBorder");
            var glass = Find<CrystalOrbSurface>(window, "CrystalSurface");

            Assert.True(root.Children.IndexOf(clock) > root.Children.IndexOf(glass));
            Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(clock.Background).Color);
            Assert.Equal(new Thickness(0), clock.BorderThickness);
            Assert.Equal(Visibility.Visible, clock.Visibility);
            Assert.False(string.IsNullOrWhiteSpace(Find<TextBlock>(window, "ClockText").Text));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CrystalHitTestingUsesSphereInsteadOfTransparentImagePixels() => RunSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"orb-crystal-{Guid.NewGuid():N}.png");
        WritePng(path, Colors.Transparent);
        var window = CreateWindow(path);
        try
        {
            var settings = Settings(80);
            window.ApplySettings(settings with
            {
                Orb = settings.Orb with { ImageShape = OrbImageShape.Contour }
            });

            Assert.False(window.IsTransparentOrbPoint(new Point(40, 40)));
            Assert.False(window.IsTransparentOrbPoint(new Point(40, 1)));
            Assert.True(window.IsTransparentOrbPoint(new Point(1, 1)));
            Assert.True(window.IsTransparentOrbPoint(new Point(81, 40)));

            window.ApplySettings(settings with
            {
                Orb = settings.Orb with
                {
                    ImageShape = OrbImageShape.Contour,
                    DisplayMode = OrbDisplayMode.Classic
                }
            });
            Assert.True(window.IsTransparentOrbPoint(new Point(40, 40)));
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task TransparentImageStillHasAnInteractiveGlassInteriorAndRim() => RunSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"orb-crystal-{Guid.NewGuid():N}.png");
        WritePng(path, Colors.Transparent);
        var window = CreateWindow(path);
        try
        {
            window.ApplySettings(Settings(56));
            var root = Find<Grid>(window, "OrbRoot");
            window.Show();
            window.UpdateLayout();
            Assert.Same(root, root.InputHitTest(new Point(28, 1)));
            Assert.NotNull(root.InputHitTest(new Point(28, 28)));
            Assert.Null(root.InputHitTest(new Point(1, 1)));
            var layout = Assert.IsType<StackPanel>(window.Content);
            window.Content = null;
            layout.Measure(new Size(96, 96));
            layout.Arrange(new Rect(0, 0, 96, 96));
            layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(layout);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(48, 48, 1, 1), pixel, 4, 0);

            Assert.True(pixel[3] > 0,
                "A layered window needs nonzero alpha inside the glass to receive native pointer hits.");
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Fact]
    public Task CrystalImageReplacementIsImmediateAndMissingImageHasGlassFallback() => RunSta(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"orb-crystal-{Guid.NewGuid():N}.png");
        WritePng(path, Colors.Red);
        var window = CreateWindow(path);
        try
        {
            window.ApplySettings(Settings(56));
            var first = Find<Image>(window, "OrbImage").Source;
            WritePng(path, Colors.Blue);
            window.ApplySettings(Settings(56));
            var second = Assert.IsAssignableFrom<BitmapSource>(Find<Image>(window, "OrbImage").Source);
            var pixel = new byte[4];
            new FormatConvertedBitmap(second, PixelFormats.Bgra32, null, 0)
                .CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.NotSame(first, second);
            Assert.Equal(255, pixel[0]);
            Assert.Equal(0, pixel[2]);

            File.Delete(path);
            window.ApplySettings(Settings(56));
            Assert.Equal(Visibility.Visible, Find<FrameworkElement>(window, "OrbCircle").Visibility);
            Assert.Equal(Visibility.Visible, Find<CrystalOrbSurface>(window, "CrystalSurface").Visibility);
            Assert.Null(Find<FrameworkElement>(window, "OrbCircle").Effect);
        }
        finally { window.Close(); File.Delete(path); }
    });

    [Theory]
    [InlineData(32, 1.0)]
    [InlineData(56, 1.25)]
    [InlineData(80, 1.5)]
    [InlineData(128, 2.0)]
    public Task CrystalRenderingHasNoAlphaOutsideSphere(int diameter, double scale) => RunSta(() =>
    {
        var window = CreateWindow();
        try
        {
            window.ApplySettings(Settings(diameter));
            var layout = Assert.IsType<StackPanel>(window.Content);
            window.Content = null;
            var length = diameter + 40;
            layout.Measure(new Size(length, length));
            layout.Arrange(new Rect(0, 0, length, length));
            layout.UpdateLayout();
            var pixelLength = (int)Math.Ceiling(length * scale);
            var bitmap = new RenderTargetBitmap(pixelLength, pixelLength, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(layout);
            var pixels = new byte[pixelLength * pixelLength * 4];
            bitmap.CopyPixels(pixels, pixelLength * 4, 0);
            var visible = 0;
            for (var y = 0; y < pixelLength; y++)
            for (var x = 0; x < pixelLength; x++)
            {
                var alpha = pixels[(y * pixelLength + x) * 4 + 3];
                if (alpha == 0) continue;
                visible++;
                var dx = (x + 0.5) / scale - length / 2.0;
                var dy = (y + 0.5) / scale - length / 2.0;
                Assert.True(dx * dx + dy * dy <= Math.Pow(diameter / 2.0 + 1.5 / scale, 2),
                    $"Unexpected exterior effect at {x},{y}, alpha={alpha}.");
            }
            Assert.True(visible > diameter * diameter * scale * scale / 2);
        }
        finally { window.Close(); }
    });

    private static AppSettings Settings(int size)
    {
        var defaults = AppSettings.CreateDefault();
        return defaults with { Orb = defaults.Orb with { SizeDip = size, DisplayMode = OrbDisplayMode.Crystal } };
    }

    private static OrbWindow CreateWindow(string? imagePath = null) => new(
        () => { }, _ => { }, () => { }, () => { }, () => { },
        () => { }, () => { }, () => { },
        imagePath ?? Path.Combine(Path.GetTempPath(), $"missing-orb-{Guid.NewGuid():N}.png"));

    private static T Find<T>(Window window, string name) where T : class =>
        Assert.IsAssignableFrom<T>(window.FindName(name));

    private static void WritePng(string path, Color color)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { color.B, color.G, color.R, color.A }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.TrySetResult(); }
            catch (Exception e) { completion.TrySetException(e); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
    }
}
