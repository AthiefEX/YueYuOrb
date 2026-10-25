namespace FloatDesk.Core.Geometry;

public readonly record struct PixelPoint(int X, int Y);

public readonly struct PixelSize
{
    public PixelSize(int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
}

public readonly record struct PixelRect
{
    public PixelRect(int left, int top, int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        try
        {
            _ = checked(left + width);
            _ = checked(top + height);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                exception,
                "The rectangle exceeds the 32-bit coordinate range.");
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);
    public PixelPoint Location => new(Left, Top);
    public PixelSize Size => new(Width, Height);
    public long PixelCount => (long)Width * Height;
}
