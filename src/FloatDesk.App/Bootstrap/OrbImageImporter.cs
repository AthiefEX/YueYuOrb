using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatDesk.App;

public static class OrbImageImporter
{
    private const long MaximumBytes = 10 * 1024 * 1024;

    public static void Import(string sourcePath, string destinationDirectory)
    {
        var frame = LoadFrame(sourcePath);
        WriteImage(
            frame,
            destinationDirectory);
    }

    public static void Import(
        string sourcePath,
        string destinationDirectory,
        Int32Rect cropArea)
    {
        var frame = LoadFrame(sourcePath);
        if (cropArea.Width <= 0 ||
            cropArea.Height <= 0 ||
            cropArea.X < 0 ||
            cropArea.Y < 0 ||
            cropArea.X + cropArea.Width > frame.PixelWidth ||
            cropArea.Y + cropArea.Height > frame.PixelHeight)
        {
            throw new InvalidDataException("裁切区域必须位于图片范围内。");
        }

        WriteImage(
            new CroppedBitmap(frame, cropArea),
            destinationDirectory);
    }

    internal static BitmapFrame LoadFrame(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length > MaximumBytes)
            throw new InvalidDataException("图片必须是 10 MiB 以内的 PNG、JPEG 或 BMP。");

        var decoder = BitmapDecoder.Create(
            new Uri(sourcePath),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if (frame.PixelWidth > 4096 || frame.PixelHeight > 4096)
            throw new InvalidDataException("图片尺寸不能超过 4096×4096。");
        return frame;
    }

    private static void WriteImage(
        BitmapSource frame,
        string destinationDirectory)
    {
        var scale = Math.Min(512.0 / frame.PixelWidth, 512.0 / frame.PixelHeight);
        BitmapSource output = scale < 1
            ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
            : frame;
        Directory.CreateDirectory(destinationDirectory);
        var temporary = Path.Combine(destinationDirectory, $"orb-{Guid.NewGuid():N}.tmp");
        using (var stream = File.Create(temporary))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            encoder.Save(stream);
        }
        File.Move(temporary, Path.Combine(destinationDirectory, "orb.png"), overwrite: true);
    }
}
