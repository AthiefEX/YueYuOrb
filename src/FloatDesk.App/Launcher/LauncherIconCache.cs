using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.Core.Launcher;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Launcher;

public sealed class LauncherIconCache : ILauncherIconProvider
{
    private readonly string _cacheDirectory;
    private readonly Func<string, CancellationToken, Task<byte[]?>> _extractPngAsync;
    private readonly Func<string, IReadOnlyList<LauncherIconSource>>?
        _resolveIconSources;
    private readonly object _gate = new();
    private readonly object _maintenanceGate = new();
    private readonly Dictionary<string, ImageSource?> _images =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lazy<Task<ImageSource?>>> _inFlight =
        new(StringComparer.Ordinal);
    private IReadOnlyList<LauncherItem>? _pendingMaintenance;
    private bool _maintenanceRunning;

    public LauncherIconCache(
        string cacheDirectory,
        Func<string, CancellationToken, Task<byte[]?>> extractPngAsync,
        Func<string, IReadOnlyList<LauncherIconSource>>? resolveIconSources = null)
    {
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _extractPngAsync = extractPngAsync ??
            throw new ArgumentNullException(nameof(extractPngAsync));
        _resolveIconSources = resolveIconSources;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public static string CreateCacheKey(
        LauncherItem item,
        Func<string, IReadOnlyList<LauncherIconSource>>? resolveIconSources = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var path = NormalizePath(item.Path);
        var sources = GetIconSources(item, resolveIconSources);
        var fingerprintParts = new List<string>
        {
            path.ToUpperInvariant(),
            item.Kind.ToString()
        };
        foreach (var source in sources)
        {
            var (length, lastWriteTicks) = ReadMetadata(source.Path);
            fingerprintParts.Add(source.Path.ToUpperInvariant());
            fingerprintParts.Add(source.Index.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            fingerprintParts.Add(length.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            fingerprintParts.Add(lastWriteTicks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        var fingerprint = string.Join('\n', fingerprintParts);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
    }

    public async Task<ImageSource?> GetAsync(
        LauncherItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = await Task.Run(
                () => CreateCacheKey(item, _resolveIconSources),
                cancellationToken)
            .ConfigureAwait(false);
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

    public void ScheduleMaintenance(IEnumerable<LauncherItem> items)
    {
        var snapshot = items.ToArray();
        lock (_maintenanceGate)
        {
            _pendingMaintenance = snapshot;
            if (_maintenanceRunning)
                return;
            _maintenanceRunning = true;
        }

        _ = Task.Run(RunMaintenanceAsync);
    }

    public Task PruneAsync(
        IEnumerable<LauncherItem> items,
        CancellationToken cancellationToken = default)
    {
        var snapshot = items.ToArray();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            PruneCore(snapshot, cancellationToken);
        }, cancellationToken);
    }

    private void PruneCore(
        IReadOnlyList<LauncherItem> items,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_cacheDirectory))
            return;

        var referenced = items
            .Select(item => CreateCacheKey(item, _resolveIconSources))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     _cacheDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extension = Path.GetExtension(path);
            var isTemporary = extension.Equals(
                ".tmp",
                StringComparison.OrdinalIgnoreCase);
            var isPng = extension.Equals(
                ".png",
                StringComparison.OrdinalIgnoreCase);
            if (!isTemporary && !isPng)
                continue;

            if (isTemporary && !IsExpiredTemporaryFile(path))
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

    private async Task RunMaintenanceAsync()
    {
        while (true)
        {
            IReadOnlyList<LauncherItem> snapshot;
            lock (_maintenanceGate)
            {
                if (_pendingMaintenance is null)
                {
                    _maintenanceRunning = false;
                    return;
                }

                snapshot = _pendingMaintenance;
                _pendingMaintenance = null;
            }

            try
            {
                foreach (var item in snapshot)
                {
                    await GetAsync(item, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                await PruneAsync(snapshot).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is IOException or
                    UnauthorizedAccessException or
                    InvalidOperationException)
            {
            }
            catch (Exception)
            {
                // Background cache maintenance is best-effort.
            }
        }
    }

    private async Task<ImageSource?> LoadAndPublishAsync(
        string key,
        LauncherItem item)
    {
        try
        {
            var cachePath = Path.Combine(_cacheDirectory, $"{key}.png");
            try
            {
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
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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

            try
            {
                await WriteAtomicallyAsync(cachePath, png)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            Publish(key, image);
            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
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
        catch (FileFormatException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<LauncherIconSource> GetIconSources(
        LauncherItem item,
        Func<string, IReadOnlyList<LauncherIconSource>>? resolveIconSources)
    {
        try
        {
            var resolved = resolveIconSources?.Invoke(item.Path);
            if (resolved is { Count: > 0 })
                return resolved;
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException)
        {
        }

        return [new(NormalizePath(item.Path), 0)];
    }

    private static (long Length, long LastWriteTicks) ReadMetadata(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return (0L, Directory.GetLastWriteTimeUtc(path).Ticks);
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

    private static bool IsExpiredTemporaryFile(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >
                TimeSpan.FromHours(1);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
