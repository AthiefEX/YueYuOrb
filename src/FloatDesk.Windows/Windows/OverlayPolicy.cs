using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Windows;

public sealed record OverlayDecision(bool Visible, bool Topmost);

public static class OverlayPolicy
{
    public static OverlayDecision Evaluate(
        bool userVisible,
        bool isFullscreen,
        bool captureActive,
        bool alwaysOnTop,
        FullscreenPolicy policy)
    {
        if (captureActive || !userVisible)
            return new(false, false);
        if (isFullscreen && policy == FullscreenPolicy.AutoHide)
            return new(false, false);
        var keepTopmost = isFullscreen && policy == FullscreenPolicy.KeepTopmost;
        return new(true, alwaysOnTop || keepTopmost);
    }
}
