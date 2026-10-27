using FloatDesk.Core.Models;

namespace FloatDesk.Core.Geometry;

/// <summary>
/// Places subtitle text immediately above or below its capture region while
/// keeping the overlay inside the monitor work area.
/// </summary>
public static class SubtitleOverlayPlacementPolicy
{
    public static PixelRect? PlaceAnchored(
        PixelRect captureRegion,
        PixelSize overlaySize,
        PixelRect workArea,
        SubtitleAttachmentMode mode,
        int gap = 8,
        bool allowCaptureOverlap = false)
    {
        if (overlaySize.Width > workArea.Width ||
            overlaySize.Height > workArea.Height)
        {
            return null;
        }

        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var safeGap = Math.Max(0, gap);
        var aboveY = (long)captureRegion.Top - safeGap - overlaySize.Height;
        var belowY = (long)captureRegion.Bottom + safeGap;
        var preferredSide = mode == SubtitleAttachmentMode.Auto
            ? PreferredAutoSide(captureRegion, workArea, safeGap)
            : mode;
        var oppositeSide = preferredSide == SubtitleAttachmentMode.Above
            ? SubtitleAttachmentMode.Below
            : SubtitleAttachmentMode.Above;
        var x = ClampX(
            captureRegion.Left +
            ((captureRegion.Width - (long)overlaySize.Width) / 2),
            workArea,
            overlaySize.Width);

        foreach (var side in new[] { preferredSide, oppositeSide })
        {
            var candidateY = side == SubtitleAttachmentMode.Above
                ? aboveY
                : belowY;
            if (TryCreateRectangle(x, candidateY, overlaySize, out var rectangle) &&
                IsInside(rectangle, workArea) &&
                !Intersects(rectangle, captureRegion))
            {
                return rectangle;
            }
        }

        if (!allowCaptureOverlap)
            return null;

        var clampedY = ClampY(
            preferredSide == SubtitleAttachmentMode.Above
                ? aboveY
                : belowY,
            workArea,
            overlaySize.Height);
        return TryCreateRectangle(x, clampedY, overlaySize, out var fallback)
            ? fallback
            : null;
    }

    private static SubtitleAttachmentMode PreferredAutoSide(
        PixelRect captureRegion,
        PixelRect workArea,
        int gap)
    {
        var aboveRoom =
            (long)captureRegion.Top - gap - workArea.Top;
        var belowRoom =
            (long)workArea.Bottom - captureRegion.Bottom - gap;
        return aboveRoom >= belowRoom
            ? SubtitleAttachmentMode.Above
            : SubtitleAttachmentMode.Below;
    }

    private static int ClampX(long value, PixelRect workArea, int width)
    {
        var minimum = workArea.Left;
        var maximum = (long)workArea.Right - width;
        return (int)Math.Clamp(value, minimum, maximum);
    }

    private static int ClampY(long value, PixelRect workArea, int height)
    {
        var minimum = workArea.Top;
        var maximum = (long)workArea.Bottom - height;
        return (int)Math.Clamp(value, minimum, maximum);
    }

    private static bool TryCreateRectangle(
        int x,
        long y,
        PixelSize size,
        out PixelRect rectangle)
    {
        if (y is < int.MinValue or > int.MaxValue)
        {
            rectangle = default;
            return false;
        }

        rectangle = new PixelRect(x, (int)y, size.Width, size.Height);
        return true;
    }

    private static bool IsInside(PixelRect candidate, PixelRect workArea) =>
        candidate.Left >= workArea.Left &&
        candidate.Top >= workArea.Top &&
        candidate.Right <= workArea.Right &&
        candidate.Bottom <= workArea.Bottom;

    private static bool Intersects(PixelRect left, PixelRect right) =>
        left.Left < right.Right &&
        left.Right > right.Left &&
        left.Top < right.Bottom &&
        left.Bottom > right.Top;
}
