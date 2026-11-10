using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

public sealed class SteamHostsLease : IAsyncDisposable
{
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _helper;

    public async Task StartAsync(SteamNetworkSettings settings, CancellationToken token)
    {
        var mask = (settings.Store ? 1 : 0) | (settings.Community ? 2 : 0) | (settings.Content ? 4 : 0);
        await StartCoreAsync(mask, token).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(int mask, CancellationToken token)
    {
        if (_pipe is not null) throw new InvalidOperationException("hosts 租约已启动。");
        var name = SteamHostsHelper.PipePrefix + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = Process.GetCurrentProcess();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "--steam-hosts-helper", name, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), mask.ToString(CultureInfo.InvariantCulture) }) info.ArgumentList.Add(argument);
        try
        {
            _helper = await Task.Run(() => Process.Start(info), token).ConfigureAwait(false) ?? throw new IOException("无法启动 hosts 管理员助手。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var clientId) || clientId != _helper.Id)
                throw new IOException("hosts 助手管道身份验证失败，未接受接入结果。");
            _reader = new StreamReader(_pipe, Encoding.UTF8, false, 1024, true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            var reply = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (reply != "READY") throw new IOException(reply ?? "hosts 助手未完成接入；请使用当前用户的管理员授权。");
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe?.IsConnected == true && _writer is not null && _reader is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _writer.WriteLineAsync("STOP".AsMemory(), timeout.Token).ConfigureAwait(false);
                var reply = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (reply != "RESTORED") throw new IOException(reply ?? "hosts 助手已断开，请检查恢复状态。");
            }
        }
        finally
        {
            try { _writer?.Dispose(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            _reader?.Dispose(); _pipe?.Dispose();
            _pipe = null; _reader = null; _writer = null;
            if (_helper is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* Do not kill the helper while it is restoring hosts. */ }
                _helper.Dispose(); _helper = null;
            }
        }
    }

    public static async Task RecoverAsync(CancellationToken token)
    {
        if (SteamHostsPatch.Owners(await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath, token).ConfigureAwait(false)).Count == 0) return;
        await using var lease = new SteamHostsLease();
        await lease.StartCoreAsync(0, token).ConfigureAwait(false);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
}
