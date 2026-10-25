using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Foreground;
using FloatDesk.Windows.Input;
using FloatDesk.Windows.Launcher;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Windows.Tests;

public sealed class PlatformAdapterTests
{
    [Fact]
    public void FullscreenCaptureStateForcesOrbHidden()
    {
        var decision = OverlayPolicy.Evaluate(
            userVisible: true,
            isFullscreen: true,
            captureActive: true,
            alwaysOnTop: true,
            policy: FullscreenPolicy.KeepTopmost);

        Assert.False(decision.Visible);
        Assert.False(decision.Topmost);
    }

    [Fact]
    public void AutoHideRestoresOnlyOriginallyVisibleOrb()
    {
        var hiddenDecision = OverlayPolicy.Evaluate(
            userVisible: true,
            isFullscreen: true,
            captureActive: false,
            alwaysOnTop: true,
            policy: FullscreenPolicy.AutoHide);
        var restored = OverlayPolicy.Evaluate(
            userVisible: true,
            isFullscreen: false,
            captureActive: false,
            alwaysOnTop: true,
            policy: FullscreenPolicy.AutoHide);
        var stillHidden = OverlayPolicy.Evaluate(
            userVisible: false,
            isFullscreen: false,
            captureActive: false,
            alwaysOnTop: true,
            policy: FullscreenPolicy.AutoHide);

        Assert.False(hiddenDecision.Visible);
        Assert.True(restored.Visible);
        Assert.False(stillHidden.Visible);
    }

    [Fact]
    public async Task GdiCaptureProducesRequestedPhysicalPixelBuffer()
    {
        using var capture = new GdiScreenCapture();

        using var frame = await capture.CaptureAsync(
            new PixelRect(0, 0, 16, 12),
            CancellationToken.None);

        Assert.Equal(16, frame.Bounds.Width);
        Assert.Equal(12, frame.Bounds.Height);
        Assert.True(frame.Bgra32.Length >= frame.Stride * 12);
    }

    [Fact]
    public void KeyboardLayoutCatalogFindsAtLeastOneLoadedLayout()
    {
        var layouts = KeyboardLayoutCatalog.GetLoadedLayouts();

        Assert.NotEmpty(layouts);
    }

    [Fact]
    public void LauncherPreservesOneArgumentPerLine()
    {
        var item = new LauncherItem(
            Guid.NewGuid(),
            "Editor",
            LauncherKind.Exe,
            @"C:\Program Files\Editor\editor.exe",
            [@"C:\my docs\a.txt", "--safe mode"],
            @"C:\Program Files\Editor",
            0);

        var spec = WindowsAppLauncher.BuildSpec(item);

        Assert.False(spec.UseShellExecute);
        Assert.Equal(item.Path, spec.FileName);
        Assert.Equal(item.Arguments, spec.Arguments);
    }

    [Fact]
    public void ForegroundIdentityIncludesProcessStartAndExecutablePath()
    {
        var first = new ForegroundSnapshot(
            1,
            100,
            101,
            42,
            43,
            new DateTimeOffset(2026, 10, 25, 1, 2, 3, TimeSpan.Zero),
            @"C:\Apps\first.exe",
            false);
        var restarted = first with
        {
            ProcessStartUtc = first.ProcessStartUtc.GetValueOrDefault().AddSeconds(1)
        };
        var replaced = first with
        {
            ExePath = @"C:\Apps\second.exe"
        };

        Assert.False(ForegroundMonitor.HasSameIdentity(first, restarted));
        Assert.False(ForegroundMonitor.HasSameIdentity(first, replaced));
    }
}
