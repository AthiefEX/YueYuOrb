using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Capture;

public sealed class GdiScreenCapture : IScreenCapture, IDisposable
{
    private const long MaximumPixels = 40_000_000;
    private const long MaximumBufferBytes = 512L * 1024 * 1024;

    public Task<CaptureFrame> CaptureAsync(
        PixelRect bounds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bounds.Width == 0 || bounds.Height == 0)
            throw new ArgumentException("Capture bounds must not be empty.", nameof(bounds));
        if (bounds.PixelCount > MaximumPixels ||
            bounds.PixelCount * 8 > MaximumBufferBytes)
        {
            throw new InvalidOperationException("Capture budget exceeded.");
        }

        using var bitmap = new Bitmap(
            bounds.Width,
            bounds.Height,
            PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bitmap.Size,
                CopyPixelOperation.SourceCopy);
        }

        var rectangle = new Rectangle(0, 0, bounds.Width, bounds.Height);
        var data = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[checked(data.Stride * data.Height)];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            for (var index = 3; index < pixels.Length; index += 4)
                pixels[index] = 255;
            return Task.FromResult(new CaptureFrame(bounds, data.Stride, pixels));
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
    }
}
