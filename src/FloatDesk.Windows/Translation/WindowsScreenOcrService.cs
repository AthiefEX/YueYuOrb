using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FloatDesk.Windows.Translation;

public sealed class WindowsScreenOcrService : IScreenOcrService, IDisposable
{
    private readonly IWindowsOcrBackend _backend;
    private readonly Lazy<IReadOnlyList<OcrLanguageOption>> _availableLanguages;
    private readonly SemaphoreSlim _recognition = new(1, 1);
    private int _disposed;

    public WindowsScreenOcrService()
        : this(new WinRtWindowsOcrBackend())
    {
    }

    internal WindowsScreenOcrService(IWindowsOcrBackend backend)
    {
        _backend = backend;
        _availableLanguages = new Lazy<IReadOnlyList<OcrLanguageOption>>(
            LoadAvailableLanguages,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyList<OcrLanguageOption> AvailableLanguages =>
        _availableLanguages.Value;

    public async Task<ScreenOcrResult> RecognizeAsync(
        CaptureFrame frame,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();

        var entered = false;
        try
        {
            await _recognition.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);

            var bitmap = PackFrame(frame);
            if (bitmap.Bgra32.All(static value => value == 0))
                return new ScreenOcrResult(null, "OcrNoText", "未识别到文字。");

            var normalizedTag = string.IsNullOrWhiteSpace(languageTag)
                ? null
                : languageTag.Trim();
            return await RunOnStaThreadAsync(
                    () => RecognizeCore(bitmap, normalizedTag, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ScreenOcrResult(null, "OcrFailed", "无法识别屏幕文字。");
        }
        finally
        {
            if (entered)
            {
                try
                {
                    _recognition.Release();
                }
                catch (ObjectDisposedException)
                {
                    // App shutdown may dispose the service while work unwinds.
                }
            }
        }
    }

    public static string NormalizeLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join(
            '\n',
            lines
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .Select(static line => line.Trim()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _recognition.Dispose();
    }

    internal static byte[] PackBgraRows(
        ReadOnlySpan<byte> source,
        int stride,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            return [];
        var rowBytes = checked(width * 4);
        if (stride < rowBytes ||
            source.Length < checked(stride * height))
        {
            throw new ArgumentException("The capture frame is incomplete.");
        }

        if (stride == rowBytes)
            return source[..checked(rowBytes * height)].ToArray();

        var packed = new byte[checked(rowBytes * height)];
        for (var row = 0; row < height; row++)
        {
            source.Slice(row * stride, rowBytes)
                .CopyTo(packed.AsSpan(row * rowBytes, rowBytes));
        }
        return packed;
    }

    private List<OcrLanguageOption> LoadAvailableLanguages()
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<OcrLanguageOption>();
            foreach (var option in _backend.AvailableLanguages)
            {
                var tag = option.LanguageTag?.Trim();
                if (string.IsNullOrEmpty(tag) || !seen.Add(tag))
                    continue;
                var displayName = string.IsNullOrWhiteSpace(option.DisplayName)
                    ? tag
                    : option.DisplayName.Trim();
                options.Add(new OcrLanguageOption(tag, displayName));
            }
            return options;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private ScreenOcrResult RecognizeCore(
        WindowsOcrBitmap bitmap,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        using var engine = CreateEngine(languageTag);
        if (engine is null)
        {
            return new ScreenOcrResult(
                null,
                "OcrLanguageUnavailable",
                "未安装可用的 Windows OCR 语言包。请在 Windows 设置中安装 OCR 语言。");
        }

        var lines = engine
            .RecognizeLinesAsync(bitmap, cancellationToken)
            .GetAwaiter()
            .GetResult();
        var text = NormalizeLines(lines);
        return text.Length == 0
            ? new ScreenOcrResult(null, "OcrNoText", "未识别到文字。")
            : new ScreenOcrResult(text, null, null);
    }

    private IWindowsOcrEngine? CreateEngine(string? languageTag)
    {
        if (languageTag is not null)
            return _backend.TryCreateFromLanguage(languageTag);

        if (_backend.TryCreateFromUserProfileLanguages() is { } profileEngine)
            return profileEngine;

        foreach (var option in AvailableLanguages)
        {
            if (_backend.TryCreateFromLanguage(option.LanguageTag) is { } engine)
                return engine;
        }
        return null;
    }

    private static WindowsOcrBitmap PackFrame(CaptureFrame frame) =>
        new(
            frame.Bounds.Width,
            frame.Bounds.Height,
            PackBgraRows(
                frame.Bgra32.Span,
                frame.Stride,
                frame.Bounds.Width,
                frame.Bounds.Height));

    private static async Task<T> RunOnStaThreadAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "FloatDesk Windows OCR"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completion.Task.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record WindowsOcrBitmap(
    int Width,
    int Height,
    byte[] Bgra32);

internal interface IWindowsOcrEngine : IDisposable
{
    Task<IReadOnlyList<string>> RecognizeLinesAsync(
        WindowsOcrBitmap bitmap,
        CancellationToken cancellationToken);
}

internal interface IWindowsOcrBackend
{
    IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; }
    IWindowsOcrEngine? TryCreateFromLanguage(string languageTag);
    IWindowsOcrEngine? TryCreateFromUserProfileLanguages();
}

internal sealed class WinRtWindowsOcrBackend : IWindowsOcrBackend
{
    public IReadOnlyList<OcrLanguageOption> AvailableLanguages
    {
        get
        {
            var options = new List<OcrLanguageOption>();
            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                try
                {
                    options.Add(new OcrLanguageOption(
                        language.LanguageTag,
                        ReadDisplayName(language)));
                }
                catch (Exception)
                {
                    // A partially registered language must not hide the rest.
                }
            }
            return options;
        }
    }

    public IWindowsOcrEngine? TryCreateFromLanguage(string languageTag)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
            return engine is null ? null : new WinRtWindowsOcrEngine(engine);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public IWindowsOcrEngine? TryCreateFromUserProfileLanguages()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            return engine is null ? null : new WinRtWindowsOcrEngine(engine);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReadDisplayName(Language language)
    {
        try
        {
            return string.IsNullOrWhiteSpace(language.DisplayName)
                ? language.LanguageTag
                : language.DisplayName;
        }
        catch (Exception)
        {
            return language.LanguageTag;
        }
    }
}

internal sealed class WinRtWindowsOcrEngine(OcrEngine engine) : IWindowsOcrEngine
{
    public async Task<IReadOnlyList<string>> RecognizeLinesAsync(
        WindowsOcrBitmap bitmap,
        CancellationToken cancellationToken)
    {
        using var softwareBitmap = CreateSoftwareBitmap(bitmap);
        var result = await engine
            .RecognizeAsync(softwareBitmap)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        return result.Lines
            .Select(static line => line.Text)
            .ToArray();
    }

    public void Dispose()
    {
    }

    private static SoftwareBitmap CreateSoftwareBitmap(WindowsOcrBitmap bitmap)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(bitmap.Bgra32);
        return SoftwareBitmap.CreateCopyFromBuffer(
            writer.DetachBuffer(),
            BitmapPixelFormat.Bgra8,
            bitmap.Width,
            bitmap.Height,
            BitmapAlphaMode.Ignore);
    }
}
