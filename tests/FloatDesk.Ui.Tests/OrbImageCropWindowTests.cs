using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class OrbImageCropWindowTests
{
    [Fact(Timeout = 10_000)]
    public async Task WindowStartsWithCenteredSquareCropAndCornerHandles()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "FloatDeskTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "source.png");
        WritePng(path, 8, 4);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new OrbImageCropWindow(path);
                var cropSize = Assert.IsType<TextBlock>(
                    window.FindName("CropSizeText"));

                Assert.Equal(2, window.CropArea.X);
                Assert.Equal(0, window.CropArea.Y);
                Assert.Equal(4, window.CropArea.Width);
                Assert.Equal(4, window.CropArea.Height);
                Assert.Contains("4 × 4 px", cropSize.Text);
                Assert.NotNull(window.FindName("TopLeftHandle"));
                Assert.NotNull(window.FindName("TopRightHandle"));
                Assert.NotNull(window.FindName("BottomLeftHandle"));
                Assert.NotNull(window.FindName("BottomRightHandle"));
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        await Task.CompletedTask;
        try
        {
            if (failure is not null)
                throw new Xunit.Sdk.XunitException(failure.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WritePng(string path, int width, int height)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[width * height * 4],
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
