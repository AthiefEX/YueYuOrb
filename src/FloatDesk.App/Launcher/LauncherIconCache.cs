using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Launcher;

public sealed class LauncherIconCache : ILauncherIconProvider
{
    private readonly string _cacheDirectory;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _extractPngAsync;
    private readonly object _gate = new();
    private readonly Dictionary<string, ImageSource?> _images =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<Task<ImageSource?>>> _inFlight =
        new(StringComparer.Ordinal);

    public LauncherIconCache(
        string cacheDirectory,
        Func<string, CancellationToken, Task<byte[]?>> extractPngAsync)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _extractPngAsync = extractPngAsync ??
            throw new ArgumentNullException(nameof(extractPngAsync));
        Directory.CreateDirectory(_cacheDirectory);
    }

    public static string CreateCacheKey(LauncherItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var path = NormalizePath(item.Path);
        var comparisonPath = path.ToUpperInvariant();
        var (length, lastWriteTicks) = ReadMetadata(item.Kind, path);
        var fingerprint = string.Join(
            '\n',
            comparisonPath,
            item.Kind.ToString(),
            length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lastWriteTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
    }

    public async Task<ImageSource?> GetAsync(
        LauncherItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateCacheKey(item);
        Lazy<Task<ImageSource?>> request;
        lock (_gate)
        {
            if (_images.TryGetValue(key, out var cached))
                return cached;
            if (!_inFlight.TryGetValue(key, out request!))
            {
                request = new Lazy<Task<ImageSource?>>(
                    () => LoadAndPublishAsync(key, item),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                _inFlight.Add(key, request);
            }
        }

        return await request.Value
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void Preload(IEnumerable<LauncherItem> items)
    {
        foreach (var item in items)
            _ = GetAsync(item, CancellationToken.None);
    }

    public void Prune(IEnumerable<LauncherItem> items)
    {
        if (!Directory.Exists(_cacheDirectory))
            return;

        var referenced = items
            .Select(CreateCacheKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     _cacheDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(path);
            var isTemporary = extension.Equals(
                ".tmp",
                StringComparison.OrdinalIgnoreCase);
            var isPng = extension.Equals(
                ".png",
                StringComparison.OrdinalIgnoreCase);
            if (!isTemporary && !isPng)
                continue;

            var key = Path.GetFileNameWithoutExtension(path);
            if (!isTemporary && referenced.Contains(key))
                continue;

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<ImageSource?> LoadAndPublishAsync(
        string key,
        LauncherItem item)
    {
        await Task.Yield();
        try
        {
            var cachePath = Path.Combine(_cacheDirectory, $"{key}.png");
            if (File.Exists(cachePath))
            {
                var cachedBytes = await File.ReadAllBytesAsync(cachePath)
                    .ConfigureAwait(false);
                var cachedImage = TryDecode(cachedBytes);
                if (cachedImage is not null)
                {
                    Publish(key, cachedImage);
                    return cachedImage;
                }

                TryDelete(cachePath);
            }

            var png = await _extractPngAsync(item.Path, CancellationToken.None)
                .ConfigureAwait(false);
            if (png is null || png.Length == 0)
            {
                Publish(key, null);
                return null;
            }

            var image = TryDecode(png);
            if (image is null)
            {
                Publish(key, null);
                return null;
            }

            await WriteAtomicallyAsync(cachePath, png).ConfigureAwait(false);
            Publish(key, image);
            return image;
        }
        catch (IOException)
        {
            Publish(key, null);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Publish(key, null);
            return null;
        }
        finally
        {
            lock (_gate)
                _inFlight.Remove(key);
        }
    }

    private void Publish(string key, ImageSource? image)
    {
        lock (_gate)
            _images[key] = image;
    }

    private async Task WriteAtomicallyAsync(string path, byte[] png)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var temporaryPath =
            $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, png)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static BitmapImage? TryDecode(byte[] bytes)
    {
        if (bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static (long Length, long LastWriteTicks) ReadMetadata(
        LauncherKind kind,
        string path)
    {
        try
        {
            if (kind == LauncherKind.Folder || Directory.Exists(path))
            {
                return (0L, Directory.Exists(path)
                    ? Directory.GetLastWriteTimeUtc(path).Ticks
                    : 0L);
            }

            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return (file.Length, file.LastWriteTimeUtc.Ticks);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }

        return (0L, 0L);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return path;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
