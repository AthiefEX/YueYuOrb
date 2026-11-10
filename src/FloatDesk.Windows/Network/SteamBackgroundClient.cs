using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FloatDesk.Windows.Network;

public sealed class SteamBackgroundClient : IAsyncDisposable
{
    private NamedPipeClientStream? _lease;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task<SteamBackgroundReply>? _completion;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;
    public bool LeaseAlive => _completion is { IsCompleted: false };

    public static async Task<SteamBackgroundReply> RequestAsync(SteamBackgroundRequest request, CancellationToken token)
    {
        using var pipe = await ConnectAsync(token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);
        return await ReadReplyAsync(reader, token).ConfigureAwait(false);
    }

    public async Task AcquireAsync(int mask, CancellationToken token)
    {
        _lease = await ConnectAsync(token).ConfigureAwait(false);
        _reader = new(_lease, Encoding.UTF8, false, 1024, true);
        _writer = new(_lease, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await _writer.WriteLineAsync(JsonSerializer.Serialize(new SteamBackgroundRequest("LEASE", mask)).AsMemory(), token).ConfigureAwait(false);
        var reply = await ReadReplyAsync(_reader, token).ConfigureAwait(false);
        if (!reply.Success || reply.Code != "Ready") { await DisposeAsync().ConfigureAwait(false); throw new IOException("后台接入失败：" + reply.Code); }
        _completion = ReadReplyAsync(_reader, _lifetime.Token);
    }

    internal static X509Certificate2 Sign(string host, string csr, string thumbprint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = RequestAsync(new("SIGN", Host: host, Csr: csr, Thumbprint: thumbprint), timeout.Token).GetAwaiter().GetResult();
        if (!reply.Success || reply.Certificate is null) throw new IOException("后台证书签发失败：" + reply.Code);
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(reply.Certificate));
    }

    public static async Task<string> StatusAsync(CancellationToken token)
    {
        try
        {
            var reply = await RequestAsync(new("STATUS"), token).ConfigureAwait(false);
            return reply.Success ? "已安装 · 后台服务就绪（计算机级）" : "未就绪 · " + reply.Code;
        }
        catch (Exception error) when (error is IOException or TimeoutException or Win32Exception or UnauthorizedAccessException or OperationCanceledException or JsonException)
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
            var installed = false;
            foreach (var certificate in store.Certificates)
                using (certificate) if (certificate.Subject.StartsWith("CN=YueYu Steam Background CA ", StringComparison.Ordinal)) installed = true;
            return installed ? "已发现证书 · 后台不可用，请安装/修复" : "未安装 · 请先安装证书";
        }
    }

    public static async Task ConfigureAsync(bool install, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add(install ? "--steam-background-install" : "--steam-background-uninstall");
        info.ArgumentList.Add(identity.User!.Value);
        using var process = Process.Start(info) ?? throw new IOException("无法启动后台配置程序。");
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("网络后台配置失败，请检查管理员权限；错误码 " + process.ExitCode);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", SteamBackgroundProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(2000, token).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != SteamBackgroundInstallation.RunningProcessId())
                throw new UnauthorizedAccessException("网络后台身份不匹配。");
            return pipe;
        }
        catch { pipe.Dispose(); throw; }
    }

    internal static async Task<string> ReadMessageAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var one = new char[1];
        while (result.Length <= SteamBackgroundProtocol.MaximumMessageLength)
        {
            if (await reader.ReadAsync(one.AsMemory(), token).ConfigureAwait(false) == 0) throw new IOException("后台连接已断开。");
            if (one[0] == '\n') return result.ToString().TrimEnd('\r');
            result.Append(one[0]);
        }
        throw new IOException("后台消息超出限制。");
    }

    private static async Task<SteamBackgroundReply> ReadReplyAsync(StreamReader reader, CancellationToken token) =>
        JsonSerializer.Deserialize<SteamBackgroundReply>(await ReadMessageAsync(reader, token).ConfigureAwait(false)) ?? throw new IOException("后台响应无效。");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_lease?.IsConnected == true && _writer is not null && _reader is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await _writer.WriteLineAsync("STOP".AsMemory(), timeout.Token).ConfigureAwait(false);
                var reply = _completion is not null
                    ? await _completion.WaitAsync(timeout.Token).ConfigureAwait(false)
                    : new SteamBackgroundReply(true, "NoLease");
                if (!reply.Success) throw new IOException("后台 hosts 恢复失败：" + reply.Code);
            }
        }
        finally
        {
            _lifetime.Cancel();
            _writer?.Dispose(); _reader?.Dispose(); _lease?.Dispose();
            _writer = null; _reader = null; _lease = null;
            if (_completion is not null)
                try { await _completion.ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            _lifetime.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
