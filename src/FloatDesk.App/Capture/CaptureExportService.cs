using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Capture;

public sealed record CaptureExportOutcome(
    OperationResult Clipboard,
    OperationResult? File,
    string? FilePath);

public static class CaptureExportService
{
    public static BitmapSource ToBitmap(CaptureFrame frame)
    {
        var pixels = frame.Bgra32.ToArray();
        var bitmap = BitmapSource.Create(
            frame.Bounds.Width,
            frame.Bounds.Height,
            frame.Bounds.Width,
            frame.Bounds.Height,
            PixelFormats.Bgra32,
            null,
            pixels,
            frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static CaptureFrame Crop(CaptureFrame source, PixelRect bounds)
    {
        var relativeLeft = checked(bounds.Left - source.Bounds.Left);
        var relativeTop = checked(bounds.Top - source.Bounds.Top);
        if (relativeLeft < 0 ||
            relativeTop < 0 ||
            bounds.Right > source.Bounds.Right ||
            bounds.Bottom > source.Bounds.Bottom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bounds),
                "Capture crop must stay inside the frozen desktop.");
        }
        var target = new byte[checked(bounds.Width * 4 * bounds.Height)];
        var sourceBytes = source.Bgra32.Span;
        var rowBytes = checked(bounds.Width * 4);
        for (var row = 0; row < bounds.Height; row++)
        {
            var sourceOffset =
                (relativeTop + row) * source.Stride + relativeLeft * 4;
            sourceBytes.Slice(sourceOffset, rowBytes)
                .CopyTo(target.AsSpan(row * rowBytes, rowBytes));
        }
        return new CaptureFrame(bounds, rowBytes, target);
    }

    public static BitmapSource Render(
        CaptureFrame frame,
        IReadOnlyList<Annotation> annotations)
    {
        var baseImage = ToBitmap(frame);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(
                baseImage,
                new Rect(0, 0, frame.Bounds.Width, frame.Bounds.Height));
            foreach (var annotation in annotations)
                DrawAnnotation(drawing, annotation);
        }
        var target = new RenderTargetBitmap(
            frame.Bounds.Width,
            frame.Bounds.Height,
            frame.Bounds.Width,
            frame.Bounds.Height,
            PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static async Task<OperationResult> CopyAsync(
        BitmapSource bitmap,
        CancellationToken cancellationToken)
    {
        var delays = new[] { 0, 50, 150, 300 };
        Exception? last = null;
        foreach (var delay in delays)
        {
            if (delay > 0)
                await Task.Delay(delay, cancellationToken);
            try
            {
                var objectData = new DataObject();
                objectData.SetImage(bitmap);
                var png = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(png);
                png.Position = 0;
                objectData.SetData("PNG", png);
                Clipboard.SetDataObject(objectData, copy: true);
                return OperationResult.Ok("已复制到剪贴板。");
            }
            catch (Exception exception) when (
                exception is System.Runtime.InteropServices.COMException or
                ExternalException)
            {
                last = exception;
            }
        }
        return OperationResult.Fail(
            "ClipboardUnavailable",
            $"剪贴板被其他程序占用：{last?.Message}");
    }

    public static async Task<OperationResult> SaveAsync(
        BitmapSource bitmap,
        string path,
        ImageFormat format,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return OperationResult.Fail("InvalidPath", "保存路径无效。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $"capture-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                BitmapEncoder encoder = format == FloatDesk.Core.Models.ImageFormat.Jpeg
                    ? new JpegBitmapEncoder { QualityLevel = 90 }
                    : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: false);
            return OperationResult.Ok(path);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            return OperationResult.Fail("FileExists", exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("SaveFailed", exception.Message);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static string CreateAutomaticPath(
        string directory,
        ImageFormat format)
    {
        Directory.CreateDirectory(directory);
        var extension = format == FloatDesk.Core.Models.ImageFormat.Jpeg
            ? ".jpg"
            : ".png";
        while (true)
        {
            var name =
                $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"[..40] +
                extension;
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
                return path;
        }
    }

    private static void DrawAnnotation(
        DrawingContext drawing,
        Annotation annotation)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(annotation.ColorHex));
        brush.Freeze();
        var start = new Point(annotation.Start.X, annotation.Start.Y);
        var end = new Point(annotation.End.X, annotation.End.Y);
        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
            {
                var rect = new Rect(start, end);
                drawing.DrawRectangle(
                    null,
                    new Pen(brush, Math.Max(1, annotation.StrokeWidthPx)),
                    rect);
                break;
            }
            case AnnotationKind.Arrow:
            {
                var pen = new Pen(brush, Math.Max(1, annotation.StrokeWidthPx))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                drawing.DrawLine(pen, start, end);
                var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                var length = Math.Max(10, annotation.StrokeWidthPx * 4);
                var left = new Point(
                    end.X - length * Math.Cos(angle - Math.PI / 6),
                    end.Y - length * Math.Sin(angle - Math.PI / 6));
                var right = new Point(
                    end.X - length * Math.Cos(angle + Math.PI / 6),
                    end.Y - length * Math.Sin(angle + Math.PI / 6));
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(end, true, true);
                    context.LineTo(left, true, false);
                    context.LineTo(right, true, false);
                }
                geometry.Freeze();
                drawing.DrawGeometry(brush, null, geometry);
                break;
            }
            case AnnotationKind.Text when !string.IsNullOrEmpty(annotation.Text):
            {
                var text = new FormattedText(
                    annotation.Text,
                    System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"),
                    Math.Max(10, annotation.FontSizePx),
                    brush,
                    1.0);
                drawing.DrawText(text, start);
                break;
            }
        }
    }
}
