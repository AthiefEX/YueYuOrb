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
    private readonly object _queueSync = new();
    private readonly object _invokeSync = new();
    private readonly Queue<RecognitionWork> _queue = new();
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Thread _worker;
    private RecognitionWork? _activeWork;
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
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "FloatDesk Windows OCR"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
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

        WindowsOcrBitmap bitmap;
        try
        {
            bitmap = PrepareBitmap(frame, _backend.MaxImageDimension);
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ScreenOcrResult(null, "OcrFailed", "无法识别屏幕文字。");
        }

        if (bitmap.Bgra32.All(static value => value == 0))
            return new ScreenOcrResult(null, "OcrNoText", "未识别到文字。");

        var normalizedTag = string.IsNullOrWhiteSpace(languageTag)
            ? null
            : languageTag.Trim();
        var work = new RecognitionWork(bitmap, normalizedTag);
        lock (_queueSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            _queue.Enqueue(work);
            Monitor.Pulse(_queueSync);
        }

        try
        {
            return await work.Completion.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            work.CancelBeforeStart(cancellationToken);
            throw;
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
        RecognitionWork[] pending;
        RecognitionWork? active;
        lock (_invokeSync)
        {
            lock (_queueSync)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                pending = _queue.ToArray();
                _queue.Clear();
                active = _activeWork;
                Monitor.PulseAll(_queueSync);
            }
            try
            {
                _workerCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The worker can finish between the disposed flag and cancel.
            }
        }

        var disposed = new ObjectDisposedException(nameof(WindowsScreenOcrService));
        foreach (var work in pending)
            work.Completion.TrySetException(disposed);
        active?.Completion.TrySetException(disposed);
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

    private ScreenOcrResult? TryCreateRecognition(
        WindowsOcrBitmap bitmap,
        string? languageTag,
        out IWindowsOcrEngine? engine,
        out Task<IReadOnlyList<string>>? recognition)
    {
        engine = CreateEngine(languageTag);
        if (engine is null)
        {
            recognition = null;
            return new ScreenOcrResult(
                null,
                "OcrLanguageUnavailable",
                "未安装可用的 Windows OCR 语言包。请在 Windows 设置中安装 OCR 语言。");
        }

        recognition = engine.RecognizeLinesAsync(
            bitmap,
            _workerCancellation.Token);
        return null;
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

    private static WindowsOcrBitmap PrepareBitmap(
        CaptureFrame frame,
        int maxImageDimension)
    {
        var width = frame.Bounds.Width;
        var height = frame.Bounds.Height;
        if (width <= 0 || height <= 0 || maxImageDimension <= 0)
            throw new ArgumentOutOfRangeException(nameof(frame));

        var packed = PackBgraRows(
            frame.Bgra32.Span,
            frame.Stride,
            width,
            height);
        if (width <= maxImageDimension && height <= maxImageDimension)
            return new WindowsOcrBitmap(width, height, packed);

        if (width >= height)
        {
            var scaledHeight = Math.Max(
                1,
                (int)Math.Round(
                    (double)height * maxImageDimension / width,
                    MidpointRounding.AwayFromZero));
            return new WindowsOcrBitmap(
                maxImageDimension,
                Math.Min(maxImageDimension, scaledHeight),
                DownscaleBgra(
                    packed,
                    width,
                    height,
                    maxImageDimension,
                    scaledHeight));
        }

        var scaledWidth = Math.Max(
            1,
            (int)Math.Round(
                (double)width * maxImageDimension / height,
                MidpointRounding.AwayFromZero));
        return new WindowsOcrBitmap(
            Math.Min(maxImageDimension, scaledWidth),
            maxImageDimension,
            DownscaleBgra(
                packed,
                width,
                height,
                scaledWidth,
                maxImageDimension));
    }

    internal static byte[] DownscaleBgra(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int targetWidth,
        int targetHeight)
    {
        if (width <= 0 || height <= 0 ||
            targetWidth <= 0 || targetHeight <= 0 ||
            targetWidth > width || targetHeight > height)
        {
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        }
        if (source.Length < checked(width * height * 4))
            throw new ArgumentException("The capture frame is incomplete.");
        if (targetWidth == width && targetHeight == height)
            return source[..checked(width * height * 4)].ToArray();

        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = ((y + 0.5) * height / targetHeight) - 0.5;
            if (sourceY < 0)
                sourceY = 0;
            else if (sourceY > height - 1)
                sourceY = height - 1;
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, height - 1);
            var wy = sourceY - y0;

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = ((x + 0.5) * width / targetWidth) - 0.5;
                if (sourceX < 0)
                    sourceX = 0;
                else if (sourceX > width - 1)
                    sourceX = width - 1;
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, width - 1);
                var wx = sourceX - x0;
                var destination = ((y * targetWidth) + x) * 4;
                var topLeft = ((y0 * width) + x0) * 4;
                var topRight = ((y0 * width) + x1) * 4;
                var bottomLeft = ((y1 * width) + x0) * 4;
                var bottomRight = ((y1 * width) + x1) * 4;

                for (var channel = 0; channel < 4; channel++)
                {
                    var top =
                        (source[topLeft + channel] * (1 - wx)) +
                        (source[topRight + channel] * wx);
                    var bottom =
                        (source[bottomLeft + channel] * (1 - wx)) +
                        (source[bottomRight + channel] * wx);
                    target[destination + channel] = (byte)Math.Round(
                        (top * (1 - wy)) + (bottom * wy),
                        MidpointRounding.AwayFromZero);
                }
            }
        }
        return target;
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                RecognitionWork work;
                lock (_queueSync)
                {
                    while (_queue.Count == 0 &&
                           Volatile.Read(ref _disposed) == 0)
                    {
                        Monitor.Wait(_queueSync);
                    }
                    if (Volatile.Read(ref _disposed) != 0 &&
                        _queue.Count == 0)
                    {
                        return;
                    }
                    work = _queue.Dequeue();
                    _activeWork = work;
                }

                if (!work.TryStart())
                {
                    ClearActive(work);
                    continue;
                }

                try
                {
                    var result = RecognizeWorkAsync(work)
                        .GetAwaiter()
                        .GetResult();
                    work.Completion.TrySetResult(result);
                }
                catch (ObjectDisposedException exception)
                {
                    work.Completion.TrySetException(exception);
                }
                catch (OperationCanceledException)
                    when (_workerCancellation.IsCancellationRequested)
                {
                    work.Completion.TrySetException(
                        new ObjectDisposedException(
                            nameof(WindowsScreenOcrService)));
                }
                catch (Exception)
                {
                    work.Completion.TrySetResult(
                        new ScreenOcrResult(
                            null,
                            "OcrFailed",
                            "无法识别屏幕文字。"));
                }
                finally
                {
                    ClearActive(work);
                }
            }
        }
        finally
        {
            _workerCancellation.Dispose();
        }
    }

    private async Task<ScreenOcrResult> RecognizeWorkAsync(
        RecognitionWork work)
    {
        IWindowsOcrEngine? engine;
        Task<IReadOnlyList<string>>? recognition;
        ScreenOcrResult? engineFailure;
        lock (_invokeSync)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            engineFailure = TryCreateRecognition(
                work.Bitmap,
                work.LanguageTag,
                out engine,
                out recognition);
        }

        if (engineFailure is not null)
            return engineFailure;

        try
        {
            var lines = await recognition!
                .ConfigureAwait(false);
            var text = NormalizeLines(lines);
            return text.Length == 0
                ? new ScreenOcrResult(null, "OcrNoText", "未识别到文字。")
                : new ScreenOcrResult(text, null, null);
        }
        finally
        {
            engine!.Dispose();
        }
    }

    private void ClearActive(RecognitionWork work)
    {
        lock (_queueSync)
        {
            if (ReferenceEquals(_activeWork, work))
                _activeWork = null;
        }
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
    int MaxImageDimension { get; }
    IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; }
    IWindowsOcrEngine? TryCreateFromLanguage(string languageTag);
    IWindowsOcrEngine? TryCreateFromUserProfileLanguages();
}

internal sealed class WinRtWindowsOcrBackend : IWindowsOcrBackend
{
    public int MaxImageDimension => checked((int)OcrEngine.MaxImageDimension);

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

internal sealed class RecognitionWork(
    WindowsOcrBitmap bitmap,
    string? languageTag)
{
    private int _state;

    public WindowsOcrBitmap Bitmap { get; } = bitmap;
    public string? LanguageTag { get; } = languageTag;
    public TaskCompletionSource<ScreenOcrResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryStart() =>
        Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    public void CancelBeforeStart(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            Completion.TrySetCanceled(cancellationToken);
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
