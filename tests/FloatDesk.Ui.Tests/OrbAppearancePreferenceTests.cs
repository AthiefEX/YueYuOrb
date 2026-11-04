using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class OrbAppearancePreferenceTests
{
    [Fact]
    public void ClockColorAndThickRingReachActualOrbWithoutClipping()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var orb = new OrbWindow(() => {}, _ => {}, () => {}, () => {}, () => {}, () => {}, () => {}, () => {});
            try
            {
                var settings = AppSettings.CreateDefault();
                orb.ApplySettings(settings with { Orb = settings.Orb with { SizeDip = 128, ShowClock = true, ClockColor = "#80FFAA33" },
                    ResourceUsage = settings.ResourceUsage with { RingsEnabled = true, RingThicknessPercent = 8 } });
                orb.Show(); orb.UpdateLayout();
                var text = (System.Windows.Controls.TextBlock)orb.FindName("ClockText");
                Assert.Equal(System.Windows.Media.Color.FromArgb(128, 255, 170, 51), ((System.Windows.Media.SolidColorBrush)text.Foreground).Color);
                var ring = (System.Windows.FrameworkElement)orb.FindName("UsageRings");
                Assert.True(ring.ActualWidth > 128 * 1.2);
                Assert.Null(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(ring));
            }
            catch (Exception ex) { failure = ex; }
            finally { orb.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ThicknessAndClockColorMergeIndependently()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        Assert.Equal(4.3, vm.ResourceRingThicknessPercent);
        Assert.Equal("#FFFFFF", vm.ClockColor);
        vm.ResourceRingThicknessPercent = 8;
        vm.ClockColor = "#FFAA33";
        var latest = initial with { Orb = initial.Orb with { ShowClock = true },
            ResourceUsage = initial.ResourceUsage with { LiquidEnabled = true } };
        var saved = vm.BuildCandidate(latest);
        Assert.True(saved.Orb.ShowClock);
        Assert.True(saved.ResourceUsage.LiquidEnabled);
        Assert.Equal(8, saved.ResourceUsage.RingThicknessPercent);
        Assert.Equal("#FFAA33", saved.Orb.ClockColor);
        Assert.Equal(saved.ResourceUsage, SettingsViewModel.From(initial).BuildCandidate(saved).ResourceUsage);
        Assert.Equal(saved.Orb.ClockColor, SettingsViewModel.From(initial).BuildCandidate(saved).Orb.ClockColor);
    }

    [Theory]
    [InlineData(2)] [InlineData(4.3)] [InlineData(8)]
    public void RingEnvelopeContainsStrokeAndHasPositiveGaps(double thickness)
    {
        var radius = ResourceUsageGeometry.RingRadius(100, thickness);
        Assert.True(radius + thickness / 2 < 50 + ResourceUsageSurface.RingExtent(100, thickness));
        var sweep = ResourceUsageGeometry.SegmentSweep(3, thickness);
        Assert.True(120 - sweep > thickness / radius * 180 / Math.PI);
    }

    [Theory]
    [InlineData("bad", 4.3)] [InlineData("#FFFFFF", 0)] [InlineData("#FFFFFF", 99)]
    public void InvalidAppearanceIsRejected(string color, double thickness)
    {
        var vm = SettingsViewModel.From(AppSettings.CreateDefault());
        vm.ClockColor = color;
        vm.ResourceRingThicknessPercent = thickness;
        Assert.False(vm.TryBuildCandidate(AppSettings.CreateDefault(), out _, out _));
    }
}
