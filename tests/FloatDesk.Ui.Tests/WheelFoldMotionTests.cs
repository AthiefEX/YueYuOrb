using System.Text.Json;
using System.Windows;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using FloatDesk.App.ViewModels;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class WheelFoldMotionTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(800, true)]
    public void InputStatusRefreshDoesNotShortenConfiguredEntrance(int duration, bool stillMoving)
    {
        RunSta(() =>
        {
            var window = new QuickPanelWindow();
            try
            {
                var settings = AppSettings.CreateDefault();
                window.Apply(settings with { General = settings.General with { WheelExpandDurationMs = duration } }, false, "");
                window.Show(); window.UpdateLayout();
                Invoke(window, "ArrangeMenu", new Point(37, 280), false);
                Invoke(window, "StartEntranceWheelMotion");
                for (var i = 0; i < 6; i++)
                {
                    Pump(50);
                    window.UpdateInputStatus(i % 2 == 0, "前台状态刷新");
                }
                var layer = (Canvas)window.FindName("WheelEntranceLayer");
                Assert.Equal(stillMoving, layer.Children.OfType<Button>().Any());
                if (stillMoving)
                {
                    Pump(600);
                    Assert.Empty(layer.Children.Cast<UIElement>());
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingBeamsDoNotChangeBrightnessOnFinalHandoff(bool left)
    {
        RunSta(() =>
        {
            var window = new QuickPanelWindow();
            try
            {
                window.Apply(AppSettings.CreateDefault(), false, "");
                window.Show(); window.UpdateLayout();
                Invoke(window, "ArrangeMenu", new Point(left ? 623 : 37, 523), left);
                window.UpdateLayout();
                Invoke(window, "StartEntranceWheelMotion");
                var property = (DependencyProperty)typeof(QuickPanelWindow).GetField("WheelFoldProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                window.BeginAnimation(property, null);
                window.SetValue(property, 1d);
                var layer = (Canvas)window.FindName("WheelFanLayer");
                layer.BeginAnimation(UIElement.OpacityProperty, null);
                layer.Opacity = 1;
                window.UpdateLayout();
                var before = BeamPixels(layer, left);
                Invoke(window, "CompleteFoldMotion");
                window.UpdateLayout();
                var after = BeamPixels(layer, left);
                Assert.Contains(before, value => value != 0);
                Assert.Equal(before, after);
            }
            finally { window.Close(); }
        });
    }

    private static byte[] BeamPixels(Canvas layer, bool left)
    {
        var bitmap = new RenderTargetBitmap(660, 560, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(layer);
        // Near the bottom anchor: overlapping beams, no buttons or labels.
        var pixels = new byte[24 * 110 * 4];
        bitmap.CopyPixels(new Int32Rect(left ? 591 : 45, 410, 24, 110), pixels, 24 * 4, 0);
        return pixels;
    }

    [Fact]
    public void VisibleWindowKeepsPairsMovingUntilConfiguredDurationAndThenRestoresScrolling()
    {
        RunSta(() =>
        {
            var window = new QuickPanelWindow();
            try
            {
                var settings = AppSettings.CreateDefault();
                window.Apply(settings with { General = settings.General with { WheelExpandDurationMs = 800 } }, false, "");
                window.Show();
                window.UpdateLayout();
                Invoke(window, "ArrangeMenu", new Point(37, 280), false);
                window.UpdateLayout();
                Invoke(window, "StartEntranceWheelMotion");
                Pump(120);
                var layer = (Canvas)window.FindName("WheelEntranceLayer");
                Assert.NotEmpty(layer.Children.OfType<Button>());
                Assert.Contains(layer.Children.OfType<Button>(), b => b.Opacity > 0);
                Pump(850);
                Assert.Empty(layer.Children.Cast<UIElement>());
                var scroller = (ScrollViewer)window.FindName("WheelScroller");
                Assert.Equal(1, scroller.Opacity);
                scroller.ScrollToVerticalOffset(100);
                window.UpdateLayout();
                Assert.True(scroller.VerticalOffset > 0);
            }
            finally { window.Close(); }
        });
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Theory]
    [InlineData(false, 37)]
    [InlineData(true, 37)]
    [InlineData(false, 280)]
    [InlineData(true, 280)]
    [InlineData(false, 523)]
    [InlineData(true, 523)]
    public void MovingBeamsFollowButtonsAndInterruptionRestoresParents(bool left, double anchorY)
    {
        RunSta(() =>
        {
            var window = new QuickPanelWindow();
            try
            {
                window.Apply(AppSettings.CreateDefault(), false, "");
                Invoke(window, "ArrangeMenu", new Point(left ? 623 : 37, anchorY), left);
                var canvas = (Canvas)window.FindName("WheelCanvas");
                var original = canvas.Children.Cast<UIElement>().ToArray();
                Invoke(window, "StartEntranceWheelMotion");
                var layer = Assert.IsType<Canvas>(window.FindName("WheelEntranceLayer"));
                var property = (DependencyProperty)typeof(QuickPanelWindow).GetField("WheelFoldProgressProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                window.BeginAnimation(property, null);
                window.SetValue(property, .5d);
                var beams = layer.Children.OfType<Polygon>().ToArray();
                Assert.NotEmpty(beams);
                foreach (var beam in beams)
                {
                    var button = Assert.IsType<Button>(beam.Tag);
                    Assert.Same(layer, button.Parent);
                    Assert.Equal(button.Opacity, beam.Opacity);
                    Assert.Equal(left ? Canvas.GetLeft(button) + button.Width : Canvas.GetLeft(button), beam.Points[1].X, 5);
                    Assert.Equal(Canvas.GetTop(button) + button.Height / 2, (beam.Points[1].Y + beam.Points[2].Y) / 2, 5);
                    Assert.True(button.RenderTransform.Value.IsIdentity, "Text must remain upright.");
                    Assert.NotNull(button.Clip);
                    Assert.IsType<LinearGradientBrush>(button.OpacityMask);
                }
                window.PrepareForScreenCapture();
                Assert.Empty(layer.Children.Cast<UIElement>());
                Assert.Equal(original, canvas.Children.Cast<UIElement>());
                Assert.All(original.OfType<Button>(), b => { Assert.Null(b.Clip); Assert.Null(b.OpacityMask); });
                Invoke(window, "StartEntranceWheelMotion");
                window.BeginAnimation(property, null);
                window.SetValue(property, 1d);
                Invoke(window, "CompleteFoldMotion");
                Assert.Empty(layer.Children.Cast<UIElement>());
                Assert.Equal(original, canvas.Children.Cast<UIElement>());
                Assert.All(original.OfType<Button>().Where(b => b.Visibility == Visibility.Visible), b => Assert.Equal(1, b.Opacity));
            }
            finally { window.Close(); }
        });
    }

    private static void Invoke(object target, string name, params object[] args) =>
        typeof(QuickPanelWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void DurationDefaultsAndRoundTripsWithoutOverwritingConcurrentSettings()
    {
        var legacy = JsonSerializer.Deserialize<GeneralSettings>("{\"StartWithWindows\":false,\"Theme\":0}")!;
        Assert.Equal(400, legacy.WheelExpandDurationMs);
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        Assert.Equal(400, vm.WheelExpandDurationMs);
        vm.WheelExpandDurationMs = 650;
        var latest = initial with { General = initial.General with { Theme = AppTheme.Dark, ShowSystemNotifications = false } };
        var saved = vm.BuildCandidate(latest);
        Assert.Equal(650, saved.General.WheelExpandDurationMs);
        Assert.Equal(AppTheme.Dark, saved.General.Theme);
        Assert.False(saved.General.ShowSystemNotifications);
        Assert.Equal(650, SettingsViewModel.From(initial).BuildCandidate(saved).General.WheelExpandDurationMs);
        Assert.Equal(650, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!.General.WheelExpandDurationMs);
    }

    [Theory]
    [InlineData(-1, 200)]
    [InlineData(200, 200)]
    [InlineData(800, 800)]
    [InlineData(9999, 800)]
    public void DurationIsBounded(int input, int expected)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.WheelExpandDurationMs = input;
        Assert.Equal(expected, vm.BuildCandidate(initial).General.WheelExpandDurationMs);
        Assert.Equal(expected, SettingsViewModel.From(initial with { General = initial.General with { WheelExpandDurationMs = input } }).WheelExpandDurationMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPairStartsAtBottomAndUnfoldsAlongMirroredArc(bool left)
    {
        var sign = left ? -1 : 1;
        var anchor = new Point(left ? 623 : 37, 280);
        var bottom = new Point(anchor.X + sign * 160, 440);
        var top = new Point(anchor.X + sign * 160, 100);
        Assert.Equal(bottom, WheelFoldMotion.Center(anchor, bottom, top, 0, left));
        Assert.Equal(top, WheelFoldMotion.Center(anchor, bottom, top, 1, left));
        var previous = bottom.Y;
        foreach (var progress in new[] { .1, .3, .5, .7, .9, 1d })
        {
            var point = WheelFoldMotion.Center(anchor, bottom, top, progress, left);
            Assert.True(point.Y < previous);
            Assert.Equal(sign, Math.Sign(point.X - anchor.X));
            previous = point.Y;
        }
        var halfway = WheelFoldMotion.Center(anchor, bottom, top, .5, left);
        Assert.True(Math.Abs(halfway.X - anchor.X) > 160, "Motion must follow an arc rather than straight translation.");
    }

    [Fact]
    public void AllPairsFinishWithinOneConfiguredDuration()
    {
        for (var index = 0; index < 8; index++)
        {
            Assert.Equal(0, WheelFoldMotion.ItemProgress(0, index, 8));
            Assert.Equal(1, WheelFoldMotion.ItemProgress(1, index, 8));
        }
        Assert.True(WheelFoldMotion.ItemProgress(.3, 0, 8) > WheelFoldMotion.ItemProgress(.3, 7, 8));
    }
}
