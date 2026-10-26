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
    public void StartupPlacementPrefersPrimaryMonitor()
    {
        var secondary = new DisplayMonitor(
            @"\\.\DISPLAY2",
            new PixelRect(1920, 0, 800, 600),
            new PixelRect(1920, 0, 800, 560),
            IsPrimary: false);
        var primary = new DisplayMonitor(
            @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080),
            new PixelRect(0, 0, 1920, 1040),
            IsPrimary: true);
        var selected = DisplayTopology.SelectPrimaryMonitor(
            [secondary, primary]);

        Assert.Same(primary, selected);
    }

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
    public void ConfiguredUsKlidResolvesAsNonImeEnglishLayout()
    {
        var result = KeyboardLayoutCatalog.Resolve("00000409");

        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.Target);
        Assert.Equal("00000409", result.Target.Klid);
        Assert.Equal(0x0409u, result.Target.LanguageId);
        Assert.False(
            KeyboardLayoutCatalog.Describe(result.Target.Hkl).IsIme);
    }

    [Fact]
    public void NonEnglishKlidIsRejectedBeforeLoading()
    {
        var result = KeyboardLayoutCatalog.Resolve("00000804");

        Assert.False(result.Success);
        Assert.Equal("LayoutNotEnglish", result.ErrorCode);
        Assert.Contains("英文", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedKlidIsRejected()
    {
        var result = KeyboardLayoutCatalog.Resolve("english");

        Assert.False(result.Success);
        Assert.Equal("LayoutInvalid", result.ErrorCode);
    }

    [Fact]
    public async Task LayoutRequestRejectsMissingTargetWindow()
    {
        var backend = new WindowsInputBackend();
        var target = new ForegroundSnapshot(
            1,
            0,
            0,
            42,
            43,
            DateTimeOffset.UtcNow,
            @"C:\Apps\editor.exe",
            false);
        var layout = new InputLayoutTarget(
            0x04090409,
            "00000409",
            0x0409);

        var result = await backend.ActivateAsync(
            target,
            layout,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TargetGone", result.ErrorCode);
    }

    [Fact]
    public void LayoutDescriptionReportsLanguageAndImeState()
    {
        var loaded = KeyboardLayoutCatalog.GetLoadedLayouts()[0];

        var description = KeyboardLayoutCatalog.Describe(loaded.Hkl);

        Assert.Equal(loaded.Hkl, description.Hkl);
        Assert.Equal(loaded.LanguageId, description.LanguageId);
        Assert.Equal(loaded.IsIme, description.IsIme);
    }

    [Fact]
    public void KeyboardSwitchGuardInstallsAndUninstallsHook()
    {
        var loaded = KeyboardLayoutCatalog.GetLoadedLayouts()[0];
        using var guard = new WindowsKeyboardSwitchGuard();

        var enabled = guard.Enable(
            new InputLayoutTarget(
                loaded.Hkl,
                loaded.Klid,
                loaded.LanguageId));
        Assert.True(enabled.Success, enabled.Message);
        Assert.True(guard.IsActive);

        guard.Disable();
        Assert.False(guard.IsActive);
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
