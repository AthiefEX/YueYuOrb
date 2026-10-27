using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class OrbImageImporterTests
{
    [Fact]
    public void ImportWithCropWritesOnlySelectedPixels()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FloatDeskTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "source.png");
            WriteTwoColorPng(sourcePath);
            var cropMethod = typeof(OrbImageImporter)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method =>
                    method.Name == nameof(OrbImageImporter.Import) &&
                    method.GetParameters() is
                    [
                        { ParameterType: var source },
                        { ParameterType: var destination },
                        { ParameterType: var crop }
                    ] &&
                    source == typeof(string) &&
                    destination == typeof(string) &&
                    crop == typeof(Int32Rect));

            Assert.NotNull(cropMethod);
            var outputDirectory = Path.Combine(directory, "output");
            cropMethod.Invoke(
                null,
                [sourcePath, outputDirectory, new Int32Rect(2, 0, 2, 2)]);

            var output = LoadPng(Path.Combine(outputDirectory, "orb.png"));
            Assert.Equal(2, output.PixelWidth);
            Assert.Equal(2, output.PixelHeight);
            Assert.Equal(Colors.Blue, ReadPixel(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteTwoColorPng(string path)
    {
        var pixels = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var offset = (y * 4 + x) * 4;
                var color = x < 2 ? Colors.Red : Colors.Blue;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        }

        var bitmap = BitmapSource.Create(
            4,
            4,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapImage LoadPng(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static Color ReadPixel(BitmapSource bitmap)
    {
        var pixels = new byte[4];
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }
}
