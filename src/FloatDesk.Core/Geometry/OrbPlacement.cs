namespace FloatDesk.Core.Geometry;

public static class OrbPlacement
{
    private const int Margin = 12;

    public static PixelRect Clamp(PixelRect orb, PixelRect workArea) =>
        SnapToWorkArea(orb, workArea, 0);

    public static PixelRect SnapToWorkArea(
        PixelRect orb,
        PixelRect workArea,
        int thresholdPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdPixels);

        var width = Math.Min(orb.Width, workArea.Width);
        var height = Math.Min(orb.Height, workArea.Height);
        var maximumLeft = checked(workArea.Right - width);
        var maximumTop = checked(workArea.Bottom - height);
        var left = Math.Clamp(orb.Left, workArea.Left, maximumLeft);
        var top = Math.Clamp(orb.Top, workArea.Top, maximumTop);

        if (left - workArea.Left <= thresholdPixels)
            left = workArea.Left;
        else if (maximumLeft - left <= thresholdPixels)
            left = maximumLeft;

        if (top - workArea.Top <= thresholdPixels)
            top = workArea.Top;
        else if (maximumTop - top <= thresholdPixels)
            top = maximumTop;

        return new PixelRect(left, top, width, height);
    }

    public static PixelPoint PlaceQuickPanel(
        PixelRect orb,
        PixelSize desiredSize,
        PixelRect workArea)
    {
        var width = Math.Min(desiredSize.Width, workArea.Width);
        var height = Math.Min(desiredSize.Height, workArea.Height);
        var maximumX = checked(workArea.Right - width);
        var x = orb.Left + width <= workArea.Right
            ? Math.Clamp(orb.Left, workArea.Left, maximumX)
            : Math.Clamp(orb.Right - width, workArea.Left, maximumX);
        var minimumY = workArea.Top + Margin;
        var maximumY = checked(workArea.Bottom - height - Margin);
        var y = maximumY < minimumY
            ? workArea.Top
            : Math.Clamp(
                orb.Top + (orb.Height - height) / 2,
                minimumY,
                maximumY);
        return new PixelPoint(x, y);
    }
}
