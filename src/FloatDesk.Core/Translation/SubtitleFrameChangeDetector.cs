using FloatDesk.Core.Models;

namespace FloatDesk.Core.Translation;

public sealed record FrameSignature(int Width, int Height, byte[] Luma);

public static class SubtitleFrameChangeDetector
{
    public static FrameSignature Create(
        CaptureFrame frame,
        int width = 64,
        int height = 36)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (frame.Bounds.Width <= 0 || frame.Bounds.Height <= 0)
        {
            throw new ArgumentException(
                "The capture frame must have a positive size.",
                nameof(frame));
        }

        var pixels = frame.Bgra32.Span;
        var luma = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(
                frame.Bounds.Height - 1,
                ((2 * y + 1) * frame.Bounds.Height) / (2 * height));
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(
                    frame.Bounds.Width - 1,
                    ((2 * x + 1) * frame.Bounds.Width) / (2 * width));
                var offset = checked(sourceY * frame.Stride + sourceX * 4);
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                luma[y * width + x] = (byte)(
                    (29 * blue + 150 * green + 77 * red) >> 8);
            }
        }

        return new FrameSignature(width, height, luma);
    }

    public static double Difference(
        FrameSignature previous,
        FrameSignature current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        EnsureComparable(previous, current);
        if (previous.Luma.Length == 0)
            return 0;

        long total = 0;
        for (var index = 0; index < previous.Luma.Length; index++)
        {
            total += Math.Abs(
                previous.Luma[index] - current.Luma[index]);
        }
        return total / (double)previous.Luma.Length / byte.MaxValue;
    }

    public static bool HasMeaningfulChange(
        FrameSignature? previous,
        FrameSignature current,
        double threshold = 0.03)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!double.IsFinite(threshold) ||
            threshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold));
        }
        return previous is null ||
               Difference(previous, current) > threshold;
    }

    private static void EnsureComparable(
        FrameSignature previous,
        FrameSignature current)
    {
        if (previous.Width != current.Width ||
            previous.Height != current.Height ||
            previous.Luma.Length != current.Luma.Length)
        {
            throw new ArgumentException(
                "Frame signatures must have matching dimensions.");
        }
    }
}
