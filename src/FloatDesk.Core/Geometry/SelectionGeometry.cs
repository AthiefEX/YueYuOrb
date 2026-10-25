namespace FloatDesk.Core.Geometry;

public static class SelectionGeometry
{
    public const int MinimumDimension = 2;

    public static PixelRect FromPoints(PixelPoint first, PixelPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var width = Math.Abs((long)second.X - first.X);
        var height = Math.Abs((long)second.Y - first.Y);

        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(second));
        }

        return new PixelRect(left, top, (int)width, (int)height);
    }

    public static bool IsUsable(PixelRect rectangle) =>
        rectangle.Width >= MinimumDimension &&
        rectangle.Height >= MinimumDimension;
}
