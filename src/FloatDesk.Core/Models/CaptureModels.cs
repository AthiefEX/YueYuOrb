using FloatDesk.Core.Geometry;

namespace FloatDesk.Core.Models;

public sealed record CaptureRequest(CaptureMode Mode, int DelaySeconds, bool OpenEditor);
public enum AnnotationKind { Rectangle, Arrow, Text }
public sealed record Annotation(
    Guid Id,
    AnnotationKind Kind,
    PixelPoint Start,
    PixelPoint End,
    string ColorHex,
    int StrokeWidthPx,
    string? Text,
    int FontSizePx)
{
    public static Annotation Rectangle(PixelPoint start, PixelPoint end, string color, int strokeWidth) =>
        new(Guid.NewGuid(), AnnotationKind.Rectangle, start, end, color, strokeWidth, null, 0);
    public static Annotation Arrow(PixelPoint start, PixelPoint end, string color, int strokeWidth) =>
        new(Guid.NewGuid(), AnnotationKind.Arrow, start, end, color, strokeWidth, null, 0);
    public static Annotation CreateText(PixelPoint start, string text, string color, int fontSize) =>
        new(Guid.NewGuid(), AnnotationKind.Text, start, start, color, 0, text, fontSize);
}

public enum CaptureSessionState
{
    Idle,
    Preparing,
    Countdown,
    Capturing,
    Selecting,
    Editing,
    Exporting,
    Restoring
}

public interface IScreenCapture
{
    Task<CaptureFrame> CaptureAsync(PixelRect bounds, CancellationToken cancellationToken);
}

public interface ICaptureExporter
{
    Task<OperationResult> CopyAsync(CaptureFrame frame, CancellationToken cancellationToken);
    Task<OperationResult> SaveAsync(
        CaptureFrame frame,
        string path,
        ImageFormat format,
        CancellationToken cancellationToken);
}

public sealed class CaptureFrame : IDisposable
{
    private byte[]? _pixels;
    public CaptureFrame(PixelRect bounds, int stride, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            stride,
            checked(bounds.Width * 4));
        if (pixels.LongLength < (long)stride * bounds.Height)
        {
            throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        }
        Bounds = bounds;
        Stride = stride;
        _pixels = pixels;
    }

    public PixelRect Bounds { get; }
    public int Stride { get; }
    public ReadOnlyMemory<byte> Bgra32
    {
        get
        {
            ObjectDisposedException.ThrowIf(_pixels is null, this);
            return _pixels;
        }
    }

    public void Dispose()
    {
        _pixels = null;
        GC.SuppressFinalize(this);
    }
}
