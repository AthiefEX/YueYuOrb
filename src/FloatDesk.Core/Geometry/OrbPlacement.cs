namespace FloatDesk.Core.Geometry;

public static class OrbPlacement
{
    private const int Margin = 12;
    private const int Gap = 10;

    public static PixelRect Clamp(PixelRect orb, PixelRect workArea)
    {
        var width = Math.Min(orb.Width, workArea.Width);
        var height = Math.Min(orb.Height, workArea.Height);
        var left = Math.Clamp(orb.Left, workArea.Left, checked(workArea.Right - width));
        var top = Math.Clamp(orb.Top, workArea.Top, checked(workArea.Bottom - height));
        return new PixelRect(left, top, width, height);
    }

    public static PixelPoint PlaceQuickPanel(
        PixelRect orb,
        PixelSize desiredSize,
        PixelRect workArea)
    {
        var width = Math.Min(desiredSize.Width, workArea.Width);
        var height = Math.Min(desiredSize.Height, workArea.Height);
        var right = checked(orb.Right + Gap);
        var left = checked(orb.Left - Gap - width);
        var x = right + width <= workArea.Right
            ? right
            : left >= workArea.Left
                ? left
                : Math.Clamp(
                    orb.Left - ((width - orb.Width) / 2),
                    workArea.Left,
                    checked(workArea.Right - width));
        var y = Math.Clamp(
            orb.Top - Margin,
            workArea.Top + Margin,
            checked(workArea.Bottom - height - Margin));
        return new PixelPoint(x, y);
    }
}
