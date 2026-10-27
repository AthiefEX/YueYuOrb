using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Launcher;

public sealed class LauncherIconExtractor : IDisposable
{
    private const int IconSize = 256;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;

    private readonly BlockingCollection<ExtractionRequest> _requests = [];
    private readonly Thread _worker;
    private bool _disposed;

    public LauncherIconExtractor()
    {
        _worker = new Thread(ProcessRequests)
        {
            IsBackground = true,
            Name = "Launcher icon extractor"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public Task<byte[]?> ExtractPngAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<byte[]?>(cancellationToken);
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return Task.FromResult<byte[]?>(null);

        var completion = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _requests.Add(
                new ExtractionRequest(path, completion, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or
                OperationCanceledException)
        {
            return exception is OperationCanceledException
                ? Task.FromCanceled<byte[]?>(cancellationToken)
                : Task.FromResult<byte[]?>(null);
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _requests.CompleteAdding();
        if (Thread.CurrentThread != _worker)
            _worker.Join(TimeSpan.FromSeconds(3));
    }

    private void ProcessRequests()
    {
        foreach (var request in _requests.GetConsumingEnumerable())
        {
            if (request.CancellationToken.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(
                    request.CancellationToken);
                continue;
            }

            byte[]? png;
            try
            {
                png = ExtractPngCore(request.Path);
            }
            catch
            {
                png = null;
            }
            request.Completion.TrySetResult(png);
        }
    }

    private static byte[]? ExtractPngCore(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return null;

        return ExtractWithShellImageFactory(path) ??
            ExtractWithFileInfo(path);
    }

    private static byte[]? ExtractWithShellImageFactory(string path)
    {
        IShellItemImageFactory? factory = null;
        nint bitmapHandle = nint.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(
                path,
                nint.Zero,
                ref interfaceId,
                out factory);
            if (result < 0 || factory is null)
                return null;

            result = factory.GetImage(
                new Size(IconSize, IconSize),
                SiigbfIconOnly | SiigbfBiggerSizeOk,
                out bitmapHandle);
            if (result < 0 || bitmapHandle == nint.Zero)
                return null;

            return EncodePngFromHBitmap(bitmapHandle);
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != nint.Zero)
                DeleteObject(bitmapHandle);
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.FinalReleaseComObject(factory);
        }
    }

    private static byte[]? ExtractWithFileInfo(string path)
    {
        var fileInfo = new ShellFileInfo();
        var result = SHGetFileInfo(
            path,
            0,
            ref fileInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShgfiIcon | ShgfiLargeIcon);
        if (result == nint.Zero || fileInfo.IconHandle == nint.Zero)
            return null;

        try
        {
            using var icon = (Icon)Icon.FromHandle(fileInfo.IconHandle).Clone();
            using var bitmap = icon.ToBitmap();
            return EncodePng(bitmap);
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(fileInfo.IconHandle);
        }
    }

    private static byte[] EncodePng(Image bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[]? EncodePngFromHBitmap(nint bitmapHandle)
    {
        if (GetObject(
                bitmapHandle,
                Marshal.SizeOf<NativeBitmap>(),
                out var nativeBitmap) == 0 ||
            nativeBitmap.Width <= 0 ||
            nativeBitmap.Height == 0)
        {
            return null;
        }

        var width = nativeBitmap.Width;
        var height = Math.Abs(nativeBitmap.Height);
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var info = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
            SizeImage = (uint)pixels.Length
        };
        var deviceContext = GetDC(nint.Zero);
        try
        {
            var scannedLines = GetDIBits(
                deviceContext,
                bitmapHandle,
                0,
                (uint)height,
                pixels,
                ref info,
                0);
            if (scannedLines != height)
                return null;
        }
        finally
        {
            _ = ReleaseDC(nint.Zero, deviceContext);
        }

        var pixelsHandle = GCHandle.Alloc(
            pixels,
            GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                width,
                height,
                stride,
                PixelFormat.Format32bppArgb,
                pixelsHandle.AddrOfPinnedObject());
            return EncodePng(bitmap);
        }
        finally
        {
            pixelsHandle.Free();
        }
    }

    private sealed record ExtractionRequest(
        string Path,
        TaskCompletionSource<byte[]?> Completion,
        CancellationToken CancellationToken);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Size size, uint flags, out nint bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal nint IconHandle;
        internal int IconIndex;
        internal uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        internal string TypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        internal int Type;
        internal int Width;
        internal int Height;
        internal int WidthBytes;
        internal ushort Planes;
        internal ushort BitsPerPixel;
        internal nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        internal uint Size;
        internal int Width;
        internal int Height;
        internal ushort Planes;
        internal ushort BitCount;
        internal uint Compression;
        internal uint SizeImage;
        internal int XPelsPerMeter;
        internal int YPelsPerMeter;
        internal uint ColorsUsed;
        internal uint ColorsImportant;
    }

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? shellItem);

    [DllImport(
        "shell32.dll",
        CharSet = CharSet.Unicode,
        PreserveSig = true)]
    private static extern nint SHGetFileInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(
        nint graphicsObject,
        int objectSize,
        out NativeBitmap bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(
        nint deviceContext,
        nint bitmap,
        uint startScan,
        uint scanLines,
        [Out] byte[] bits,
        ref BitmapInfoHeader bitmapInfo,
        uint usage);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint graphicsObject);
}
