using System.Windows;

namespace FloatDesk.App;

public enum OrbCropHandle
{
    Move,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

public readonly record struct OrbCropSelection(int X, int Y, int Size)
{
    private const int PreferredMinimumSize = 32;

    public static OrbCropSelection CreateCentered(
        int imageWidth,
        int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(imageWidth),
                "图片尺寸必须大于零。");

        var size = Math.Min(imageWidth, imageHeight);
        return new OrbCropSelection(
            (imageWidth - size) / 2,
            (imageHeight - size) / 2,
            size);
    }

    public OrbCropSelection Move(
        int deltaX,
        int deltaY,
        int imageWidth,
        int imageHeight)
    {
        var maximumX = Math.Max(0, imageWidth - Size);
        var maximumY = Math.Max(0, imageHeight - Size);
        return this with
        {
            X = Math.Clamp(X + deltaX, 0, maximumX),
            Y = Math.Clamp(Y + deltaY, 0, maximumY)
        };
    }

    public OrbCropSelection Resize(
        OrbCropHandle handle,
        int deltaX,
        int deltaY,
        int imageWidth,
        int imageHeight)
    {
        if (handle == OrbCropHandle.Move)
            return Move(deltaX, deltaY, imageWidth, imageHeight);

        var horizontalDirection = handle is
            OrbCropHandle.TopRight or OrbCropHandle.BottomRight
                ? 1
                : -1;
        var verticalDirection = handle is
            OrbCropHandle.BottomLeft or OrbCropHandle.BottomRight
                ? 1
                : -1;
        var requestedSize = Size + (int)Math.Round(
            ((horizontalDirection * deltaX) +
             (verticalDirection * deltaY)) / 2.0);
        var right = X + Size;
        var bottom = Y + Size;
        var maximumSize = handle switch
        {
            OrbCropHandle.TopLeft => Math.Min(right, bottom),
            OrbCropHandle.TopRight => Math.Min(imageWidth - X, bottom),
            OrbCropHandle.BottomLeft => Math.Min(right, imageHeight - Y),
            _ => Math.Min(imageWidth - X, imageHeight - Y)
        };
        var minimumSize = Math.Min(
            PreferredMinimumSize,
            Math.Min(imageWidth, imageHeight));
        var size = Math.Clamp(requestedSize, minimumSize, maximumSize);
        return new OrbCropSelection(
            horizontalDirection < 0 ? right - size : X,
            verticalDirection < 0 ? bottom - size : Y,
            size);
    }

    public Int32Rect ToBounds() => new(X, Y, Size, Size);
}
