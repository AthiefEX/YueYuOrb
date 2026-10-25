using FloatDesk.Core.Capture;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class AnnotationDocumentTests
{
    [Fact]
    public void UndoThenRedoRestoresAnnotation()
    {
        var document = new AnnotationDocument();
        var item = Annotation.CreateText(
            new PixelPoint(10, 20),
            "中文说明",
            "#FF3B30",
            20);

        document.Add(item);
        Assert.Single(document.Items);
        Assert.True(document.Undo());
        Assert.Empty(document.Items);
        Assert.True(document.Redo());
        Assert.Equal("中文说明", Assert.Single(document.Items).Text);
    }

    [Fact]
    public void NewEditClearsRedoBranch()
    {
        var document = new AnnotationDocument();
        document.Add(Annotation.Rectangle(
            new PixelPoint(0, 0),
            new PixelPoint(10, 10),
            "#FF3B30",
            3));
        document.Undo();
        document.Add(Annotation.Arrow(
            new PixelPoint(1, 1),
            new PixelPoint(8, 8),
            "#FF3B30",
            3));

        Assert.False(document.Redo());
        Assert.Equal(AnnotationKind.Arrow, Assert.Single(document.Items).Kind);
    }
}
