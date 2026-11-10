using System.Runtime.InteropServices;
using System.Text;

namespace FloatDesk.Windows.Assistant;

public sealed class AssistantMicrophone : IDisposable
{
    private readonly object _sync = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly List<Buffer> _buffers = [];
    private readonly MemoryStream _pcm = new();
    private nint _handle;
    private Task? _worker;
    private Task<byte[]>? _stopping;
    private bool _stop;
    private bool _disposed;
    private int _limit;
    private Exception? _workerError;
    public event Action? LimitReached;
    public static bool HasInputDevice => WaveInGetNumDevs() > 0;
    public bool IsRecording { get { lock (_sync) return _handle != 0 && _stopping is null; } }

    public void Start(int maximumSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (maximumSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        lock (_sync)
        {
            if (_handle != 0) throw new InvalidOperationException("麦克风正在使用。");
            if (!HasInputDevice) throw new InvalidOperationException("未检测到麦克风，请检查设备和Windows麦克风权限。");
            _limit = maximumSeconds * 32000; _stop = false; _stopping = null; _workerError = null; _pcm.SetLength(0);
            var format = new WaveFormat { Format = 1, Channels = 1, SamplesPerSecond = 16000, AverageBytesPerSecond = 32000, BlockAlign = 2, BitsPerSample = 16 };
            Check(WaveInOpen(out _handle, uint.MaxValue, ref format, _signal.SafeWaitHandle.DangerousGetHandle(), 0, 0x00050000));
            try
            {
                for (var index = 0; index < 2; index++)
                {
                    var buffer = new Buffer(); _buffers.Add(buffer);
                    Check(WaveInPrepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>())); buffer.Prepared = true;
                    Check(WaveInAddBuffer(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()));
                }
                Check(WaveInStart(_handle)); _worker = Task.Run(CaptureLoop);
            }
            catch { _stop = true; _ = WaveInReset(_handle); ReleaseBuffers(); throw; }
        }
    }

    private void CaptureLoop()
    {
        try
        {
            while (true)
            {
                _signal.WaitOne(100); var reached = false;
                lock (_sync)
                {
                    CollectBuffers(requeue: !_stop);
                    if (_pcm.Length >= _limit && !_stop) { _stop = true; reached = true; }
                    if (_stop && !reached) break;
                }
                if (reached) { LimitReached?.Invoke(); break; }
            }
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        { lock (_sync) { _workerError = error; _stop = true; } LimitReached?.Invoke(); }
    }

    private void CollectBuffers(bool requeue)
    {
        foreach (var buffer in _buffers)
        {
            var header = Marshal.PtrToStructure<WaveHeader>(buffer.Header);
            if ((header.Flags & 1) == 0 || buffer.Consumed) continue;
            var count = Math.Min((int)header.BytesRecorded, Math.Max(0, _limit - (int)_pcm.Length));
            if (count > 0) _pcm.Write(buffer.Bytes, 0, count);
            buffer.Consumed = true;
            if (requeue && _pcm.Length < _limit)
            { Check(WaveInAddBuffer(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>())); buffer.Consumed = false; }
        }
    }

    public Task<byte[]> StopAsync()
    {
        lock (_sync)
        {
            if (_handle == 0) return Task.FromResult(Array.Empty<byte>());
            return _stopping ??= StopCoreAsync();
        }
    }

    private async Task<byte[]> StopCoreAsync()
    {
        lock (_sync) _stop = true;
        _ = WaveInStop(_handle); var reset = WaveInReset(_handle); _signal.Set();
        if (_worker is { } worker) await worker.ConfigureAwait(false);
        lock (_sync)
        {
            try
            {
                Check(reset);
                CollectBuffers(requeue: false);
                if (_workerError is not null) throw new InvalidOperationException("录音未完成，请重新检查麦克风。");
                var pcm = _pcm.ToArray();
                try { return EncodeWave(pcm); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(pcm); }
            }
            finally { try { ReleaseBuffers(); } finally { _pcm.GetBuffer().AsSpan().Clear(); _pcm.SetLength(0); } }
        }
    }

    private void ReleaseBuffers()
    {
        foreach (var buffer in _buffers.ToArray())
        {
            if (buffer.Prepared && WaveInUnprepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()) != 0)
            {
                _ = WaveInReset(_handle);
                if (WaveInUnprepareHeader(_handle, buffer.Header, (uint)Marshal.SizeOf<WaveHeader>()) != 0) continue;
            }
            buffer.Dispose(); _buffers.Remove(buffer);
        }
        // On driver failure retain pinned/native buffers rather than freeing memory still owned by WinMM.
        if (_buffers.Count != 0) throw new InvalidOperationException("麦克风驱动尚未释放录音缓冲区。");
        if (_handle != 0) Check(WaveInClose(_handle)); _handle = 0;
    }

    public static byte[] EncodeWave(byte[] pcm)
    {
        if (pcm.Length % 2 != 0 || pcm.Length > 120 * 32000) throw new ArgumentException("录音长度或PCM采样格式无效。", nameof(pcm));
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm); return output.ToArray();
    }

    private static void Check(uint code) { if (code != 0) throw new InvalidOperationException($"麦克风操作未完成（WinMM {code}），请检查设备和Windows麦克风权限。"); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); }
        finally { lock (_sync) { if (_handle != 0) { _ = WaveInReset(_handle); ReleaseBuffers(); } _pcm.Dispose(); _signal.Dispose(); } }
    }

    private sealed class Buffer : IDisposable
    {
        public byte[] Bytes { get; } = new byte[3200];
        private readonly GCHandle _pin;
        public nint Header { get; }
        public bool Prepared { get; set; }
        public bool Consumed { get; set; }
        public Buffer()
        { _pin = GCHandle.Alloc(Bytes, GCHandleType.Pinned); Header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>()); Marshal.StructureToPtr(new WaveHeader { Data = _pin.AddrOfPinnedObject(), BufferLength = (uint)Bytes.Length }, Header, false); }
        public void Dispose() { Bytes.AsSpan().Clear(); Marshal.FreeHGlobal(Header); if (_pin.IsAllocated) _pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct WaveFormat
    { public ushort Format, Channels; public uint SamplesPerSecond, AverageBytesPerSecond; public ushort BlockAlign, BitsPerSample, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WaveHeader
    { public nint Data; public uint BufferLength, BytesRecorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs")] private static extern uint WaveInGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveInOpen")] private static extern uint WaveInOpen(out nint handle, uint deviceId, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll", EntryPoint = "waveInPrepareHeader")] private static extern uint WaveInPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")] private static extern uint WaveInUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInAddBuffer")] private static extern uint WaveInAddBuffer(nint handle, nint header, uint size);
    [DllImport("winmm.dll", EntryPoint = "waveInStart")] private static extern uint WaveInStart(nint handle);
    [DllImport("winmm.dll", EntryPoint = "waveInStop")] private static extern uint WaveInStop(nint handle);
    [DllImport("winmm.dll", EntryPoint = "waveInReset")] private static extern uint WaveInReset(nint handle);
    [DllImport("winmm.dll", EntryPoint = "waveInClose")] private static extern uint WaveInClose(nint handle);
}

public sealed class AssistantSpeaker : IDisposable
{
    private object? _voice;
    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("系统没有可用朗读组件。");
        _voice ??= Activator.CreateInstance(type) ?? throw new InvalidOperationException("系统朗读组件无法启动。");
        // Async + purge previous + plain text: model output must not be interpreted as SAPI XML.
        dynamic voice = _voice; voice.Speak(text[..Math.Min(16000, text.Length)], 19);
    }
    public void Stop() { if (_voice is { } instance) { dynamic voice = instance; voice.Speak("", 2); } }
    public void Dispose()
    {
        try { Stop(); }
        finally { if (_voice is { } voice && Marshal.IsComObject(voice)) Marshal.FinalReleaseComObject(voice); _voice = null; }
    }
}
