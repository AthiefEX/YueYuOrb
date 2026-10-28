using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Translation;
using Windows.Globalization;
using Windows.Media.Ocr;
using Xunit.Abstractions;

namespace FloatDesk.Windows.Tests;

public sealed class WindowsScreenOcrNativeTests(ITestOutputHelper output)
{
    // Generated fixtures only: never capture the desktop or log recognized text.
    [ChineseOcrTheory]
    [Trait("Category", "NativeOcr")]
    [InlineData(false, 10, 300, 24, false, false, false)]
    [InlineData(false, 12, 100, 24, false, false, false)]
    [InlineData(false, 14, 600, 32, true, false, false)]
    [InlineData(false, 14, 300, 40, false, true, false)]
    [InlineData(true, 12, 100, 24, false, false, false)]
    [InlineData(true, 12, 300, 40, false, false, false)]
    [InlineData(true, 14, 600, 32, true, false, false)]
    [InlineData(true, 14, 300, 24, false, true, false)]
    [InlineData(true, 14, 300, 80, false, false, true)]
    [InlineData(false, 14, 300, 80, false, false, true)]
    public async Task GeneratedSmallTextIsRecognized(
        bool chinese, int size, int width, int height,
        bool zeroAlpha, bool dark, bool multiline)
    {
        using var service = new WindowsScreenOcrService();
        using var frame = GeneratedFrame(chinese, size, width, height, zeroAlpha, dark, multiline);
        var profileTag = OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage.LanguageTag;
        output.WriteLine("Installed=" + string.Join(",", service.AvailableLanguages.Select(x => x.LanguageTag)));
        output.WriteLine("Profile=" + profileTag);
        // Auto mode must preserve the machine's profile language. Only expect
        // Chinese fixtures in auto mode when that profile is also Chinese.
        var tags = profileTag?.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) == true
            ? new string?[] { null, "zh-Hans-CN" }
            : new string?[] { "zh-Hans-CN" };
        foreach (var tag in tags)
        {
            var result = await service.RecognizeAsync(frame, tag, CancellationToken.None);
            output.WriteLine($"fixture={(chinese ? "CJK" : "Latin")} font={size}px image={width}x{height} alpha={(zeroAlpha ? 0 : 255)} dark={dark} multiline={multiline} tag={tag ?? "auto"} chars={result.Text?.Length ?? 0} error={result.ErrorCode ?? "none"}");
            Assert.True(result.ErrorCode is null, $"Generated fixture failed: {result.ErrorCode}");
            Assert.True(!string.IsNullOrWhiteSpace(result.Text), "Generated fixture returned no text.");
            Assert.True(chinese
                ? result.Text!.Any(c => c is >= '\u4E00' and <= '\u9FFF')
                : result.Text!.Any(char.IsAsciiLetter), "Expected fixture script was not recognized.");
        }
    }

    private static CaptureFrame GeneratedFrame(
        bool chinese, int size, int width, int height,
        bool zeroAlpha, bool dark, bool multiline)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(chinese ? "Microsoft YaHei" : "Segoe UI", size, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            graphics.Clear(dark ? Color.Black : Color.White);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var text = chinese ? "静态测试文字识别" : "Small text recognition sample";
            graphics.DrawString(multiline ? text + "\n" + text : text,
                font, dark ? Brushes.White : Brushes.Black, 0, 0, StringFormat.GenericTypographic);
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            if (zeroAlpha)
                for (var i = 3; i < pixels.Length; i += 4)
                    pixels[i] = 0;
            return new CaptureFrame(new PixelRect(0, 0, width, height), data.Stride, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private sealed class ChineseOcrTheoryAttribute : TheoryAttribute
    {
        public ChineseOcrTheoryAttribute()
        {
            if (!OcrEngine.IsLanguageSupported(new Language("zh-Hans-CN")))
                Skip = "Requires an installed Windows simplified Chinese OCR language pack.";
        }
    }
}
