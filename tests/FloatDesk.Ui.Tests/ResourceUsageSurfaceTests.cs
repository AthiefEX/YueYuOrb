using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class ResourceUsageSurfaceTests
{
    [Theory]
    [InlineData(32)] [InlineData(56)] [InlineData(128)]
    public void RingRemainsInsideSurfaceAndTurnsRedAtNinety(int size)
    {
        Sta(() =>
        {
            var surface = new ResourceUsageSurface { RingsOnly = true };
            surface.Configure(new() { RingsEnabled = true, RingCpu = false, RingGpu = false }, size);
            surface.SetSample(new(null, 95, 95, 100, []));
            var bitmap = Render(surface, size * 1.14);
            var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
            Assert.Contains(Enumerable.Range(0, bytes.Length / 4), i => bytes[i * 4 + 2] > 180 && bytes[i * 4 + 1] < 130 && bytes[i * 4 + 3] > 200);
            var bounds = VisualTreeHelper.GetDrawing(surface)!.Bounds;
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0);
            Assert.True(bounds.Right <= surface.ActualWidth && bounds.Bottom <= surface.ActualHeight);
        });
    }

    [Fact]
    public void HalfLiquidLeavesUpperPartTransparentAndUnavailableDoesNotShowFakeFill()
    {
        Sta(() =>
        {
            var surface = new ResourceUsageSurface();
            surface.Configure(new() { LiquidEnabled = true }, 100);
            surface.SetSample(new(null, 50, 50, 100, []));
            var bitmap = Render(surface, 100);
            var pixels = new byte[100 * 100 * 4]; bitmap.CopyPixels(pixels, 400, 0);
            Assert.Equal(0, pixels[(20 * 100 + 50) * 4 + 3]);
            Assert.True(pixels[(80 * 100 + 50) * 4 + 3] > 0);
            surface.SetSample(ResourceUsageSnapshot.Empty);
            Render(surface, 100).CopyPixels(pixels, 400, 0);
            Assert.All(pixels, b => Assert.Equal(0, b));
        });
    }

    [Fact]
    public void OrbModesKeepClockAndCrystalAndRingHitArea()
    {
        Sta(() =>
        {
            var orb = new OrbWindow(() => {}, _ => {}, () => {}, () => {}, () => {}, () => {}, () => {}, () => {});
            try
            {
                var initial = AppSettings.CreateDefault();
                foreach (var mode in new[] { OrbDisplayMode.Classic, OrbDisplayMode.Crystal })
                {
                    orb.ApplySettings(initial with { Orb = initial.Orb with { DisplayMode = mode, ShowClock = true },
                        ResourceUsage = new() { LiquidEnabled = true, RingsEnabled = true } });
                    Assert.Equal(Visibility.Visible, ((FrameworkElement)orb.FindName("ClockBorder")).Visibility);
                    Assert.Equal(mode == OrbDisplayMode.Crystal ? Visibility.Visible : Visibility.Collapsed,
                        ((FrameworkElement)orb.FindName("CrystalSurface")).Visibility);
                    Assert.False(orb.IsTransparentOrbPoint(new Point(28, -2.5)));
                    Assert.True(orb.IsTransparentOrbPoint(new Point(-5, -5)));
                    orb.Show(); orb.UpdateLayout();
                    var rings = (FrameworkElement)orb.FindName("UsageRings");
                    var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(rings);
                    Assert.True(clip is null || clip.Bounds.Width >= rings.ActualWidth,
                        "The actual orb layout must not crop the outer ring.");
                    if (Environment.GetEnvironmentVariable("FLOATDESK_RESOURCE_RENDER_DIR") is { Length: > 0 } directory)
                    {
                        var sample = new ResourceUsageSnapshot(95, 50, 8, 16, [new("gpu", "测试显卡", 35)]);
                        ((ResourceUsageSurface)orb.FindName("UsageLiquid")).SetSample(sample);
                        ((ResourceUsageSurface)orb.FindName("UsageRings")).SetSample(sample);
                        orb.UpdateLayout();
                        var capture = new RenderTargetBitmap((int)Math.Ceiling(orb.ActualWidth), (int)Math.Ceiling(orb.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        capture.Render(orb);
                        Directory.CreateDirectory(directory);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(capture));
                        using var stream = File.Create(Path.Combine(directory, $"orb-{mode}.png")); encoder.Save(stream);
                    }
                }
                orb.ApplySettings(initial);
                Assert.True(orb.IsTransparentOrbPoint(new Point(28, -2.5)));
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)orb.FindName("UsageRings")).Visibility);
            }
            finally { orb.Close(); }
        });
    }

    [Fact]
    public void CollapsedOrbSuspendsSamplingAndDrawing()
    {
        Sta(() =>
        {
            var orb = new OrbWindow(() => {}, _ => {}, () => {}, () => {}, () => {}, () => {}, () => {}, () => {});
            try
            {
                var settings = AppSettings.CreateDefault() with { ResourceUsage = new() { LiquidEnabled = true } };
                orb.ApplySettings(settings); orb.Show(); orb.UpdateLayout();
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(OrbWindow).GetField("_edgeCollapsed", flags)!.SetValue(orb, true);
                typeof(OrbWindow).GetMethod("RefreshResourceSampling", flags)!.Invoke(orb, null);
                Assert.Null(typeof(OrbWindow).GetField("_resourceCancellation", flags)!.GetValue(orb));
                var liquid = (ResourceUsageSurface)orb.FindName("UsageLiquid");
                Assert.False((bool)typeof(ResourceUsageSurface).GetField("_subscribed", flags)!.GetValue(liquid)!);
                typeof(OrbWindow).GetField("_edgeCollapsed", flags)!.SetValue(orb, false);
                typeof(OrbWindow).GetMethod("RefreshResourceSampling", flags)!.Invoke(orb, null);
                Assert.NotNull(typeof(OrbWindow).GetField("_resourceCancellation", flags)!.GetValue(orb));
            }
            finally { orb.Close(); }
        });
    }

    [Fact]
    public void ContourImageTransparencyAlsoMasksLiquid()
    {
        Sta(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            var pixels = new byte[4 * 4 * 4];
            for (var y = 0; y < 4; y++) for (var x = 0; x < 2; x++) pixels[(y * 4 + x) * 4 + 3] = 255;
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16)));
            using (var stream = File.Create(path)) png.Save(stream);
            var orb = new OrbWindow(() => {}, _ => {}, () => {}, () => {}, () => {}, () => {}, () => {}, () => {}, path);
            try
            {
                var settings = AppSettings.CreateDefault();
                orb.ApplySettings(settings with { Orb = settings.Orb with { SizeDip = 100, ImageShape = OrbImageShape.Contour },
                    ResourceUsage = new() { LiquidEnabled = true } });
                var surface = (ResourceUsageSurface)orb.FindName("UsageLiquid");
                surface.SetSample(new(null, 100, 100, 100, []));
                var bitmap = Render(surface, 100);
                var result = new byte[40000]; bitmap.CopyPixels(result, 400, 0);
                Assert.True(result[(50 * 100 + 25) * 4 + 3] > 0);
                Assert.Equal(0, result[(50 * 100 + 75) * 4 + 3]);
            }
            finally { orb.Close(); File.Delete(path); }
        });
    }

    private static RenderTargetBitmap Render(FrameworkElement surface, double size)
    {
        surface.Measure(new Size(size, size)); surface.Arrange(new Rect(0, 0, size, size)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size), (int)Math.Ceiling(size), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        if (Environment.GetEnvironmentVariable("FLOATDESK_RESOURCE_RENDER_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            var name = surface is ResourceUsageSurface s && s.RingsOnly ? "rings" : "liquid";
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"{name}-{size:F0}.png")); encoder.Save(stream);
        }
        return bitmap;
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
