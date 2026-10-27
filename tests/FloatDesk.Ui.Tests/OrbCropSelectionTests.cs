using System.Reflection;
using FloatDesk.App;

namespace FloatDesk.Ui.Tests;

public sealed class OrbCropSelectionTests
{
    [Fact]
    public void CenteredSquareAndMoveStayInsideImage()
    {
        var type = typeof(OrbImageImporter).Assembly.GetType(
            "FloatDesk.App.OrbCropSelection");
        Assert.NotNull(type);

        var centered = type.GetMethod(
            "CreateCentered",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(int), typeof(int)])!.Invoke(null, [12, 8]);
        var moved = type.GetMethod(
            "Move",
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(int), typeof(int), typeof(int), typeof(int)])!
            .Invoke(centered, [100, 0, 12, 8]);

        Assert.Equal(2, ReadInt(type, centered, "X"));
        Assert.Equal(0, ReadInt(type, centered, "Y"));
        Assert.Equal(8, ReadInt(type, centered, "Size"));
        Assert.Equal(4, ReadInt(type, moved, "X"));
        Assert.Equal(0, ReadInt(type, moved, "Y"));
        Assert.Equal(8, ReadInt(type, moved, "Size"));
    }

    [Fact]
    public void CornerResizeKeepsOppositeCornerFixedAndClampsToImage()
    {
        var selection = new OrbCropSelection(10, 10, 80);

        var topLeft = selection.Resize(
            OrbCropHandle.TopLeft,
            20,
            20,
            100,
            100);
        var bottomRight = topLeft.Resize(
            OrbCropHandle.BottomRight,
            20,
            20,
            100,
            100);

        Assert.Equal(30, topLeft.X);
        Assert.Equal(30, topLeft.Y);
        Assert.Equal(60, topLeft.Size);
        Assert.Equal(30, bottomRight.X);
        Assert.Equal(30, bottomRight.Y);
        Assert.Equal(70, bottomRight.Size);
    }

    private static int ReadInt(Type type, object? instance, string property) =>
        (int)(type.GetProperty(property)?.GetValue(instance)
            ?? throw new InvalidOperationException(
                $"Property {property} was not found."));
}
