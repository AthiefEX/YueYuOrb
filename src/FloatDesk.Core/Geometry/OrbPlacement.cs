namespace FloatDesk.Core.Geometry;

public static class OrbPlacement
{
    private const int Margin = 12;

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
