namespace FloatDesk.Core.Geometry;

/// <summary>
/// Finds a subtitle-overlay position that does not overlap a capture region.
/// Placement is considered valid only when the whole overlay fits inside the
/// target work area.
/// </summary>
public static class TranslationOverlayPlacementPolicy
{
    public static PixelRect? PlaceOutside(
        PixelRect captureRegion,
        PixelSize overlaySize,
        PixelRect workArea,
        PixelPoint preferredTopLeft,
        int gap = 8)
    {
        if (overlaySize.Width > workArea.Width ||
            overlaySize.Height > workArea.Height)
        {
            return null;
        }

        var safeGap = Math.Max(0, gap);
        var clampedX = Clamp(
            preferredTopLeft.X,
            workArea.Left,
            workArea.Right - overlaySize.Width);
        var clampedY = Clamp(
            preferredTopLeft.Y,
            workArea.Top,
            workArea.Bottom - overlaySize.Height);

        var candidates = new[]
        {
            new PixelPoint(clampedX, clampedY),
            CreatePoint(
                clampedX,
                (long)captureRegion.Bottom + safeGap),
            CreatePoint(
                clampedX,
                (long)captureRegion.Top - safeGap - overlaySize.Height),
            CreatePoint(
                (long)captureRegion.Right + safeGap,
                clampedY),
            CreatePoint(
                (long)captureRegion.Left - safeGap - overlaySize.Width,
                clampedY)
        };

        foreach (var candidate in candidates)
        {
            if (candidate is not { } point)
                continue;

            if (!TryCreateRectangle(point, overlaySize, out var rectangle))
                continue;
            if (IsInside(rectangle, workArea) &&
                !Intersects(rectangle, captureRegion))
            {
                return rectangle;
            }
        }

        return null;
    }

    private static PixelPoint? CreatePoint(long x, long y) =>
        x is < int.MinValue or > int.MaxValue ||
        y is < int.MinValue or > int.MaxValue
            ? null
            : new PixelPoint((int)x, (int)y);

    private static bool TryCreateRectangle(
        PixelPoint point,
        PixelSize size,
        out PixelRect rectangle)
    {
        var right = (long)point.X + size.Width;
        var bottom = (long)point.Y + size.Height;
        if (right > int.MaxValue || bottom > int.MaxValue)
        {
            rectangle = default;
            return false;
        }

        rectangle = new PixelRect(
            point.X,
            point.Y,
            size.Width,
            size.Height);
        return true;
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum
            ? minimum
            : Math.Clamp(value, minimum, maximum);

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
