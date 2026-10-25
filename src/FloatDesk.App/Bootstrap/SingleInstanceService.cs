using System.IO.Pipes;
using System.IO;
using FloatDesk.Core.Models;

namespace FloatDesk.App;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\FloatDesk.SingleInstance";
    private const string PipeName = "FloatDesk.ShowSettings";
    private Mutex? _mutex;
    private FileStream? _dataLock;
    private CancellationTokenSource? _listenerCts;

    public async Task<OperationResult> TryAcquireAsync(CancellationToken cancellationToken)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return OperationResult.Fail("AlreadyRunning", "FloatDesk 已在当前会话运行。");

        var paths = new AppDataPaths();
        paths.EnsureCreated();
        try
        {
            _dataLock = new FileStream(
                paths.LockFile,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            _dataLock.SetLength(0);
            await using var writer = new StreamWriter(_dataLock, leaveOpen: true);
            await writer.WriteAsync(Environment.UserName.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return OperationResult.Fail(
                "DataLockedOtherSession",
                "FloatDesk 已在另一个 Windows 会话运行；请回到原会话使用。");
        }

        _listenerCts = new CancellationTokenSource();
        _ = ListenAsync(_listenerCts.Token);
        return OperationResult.Ok();
    }

    public static async Task SignalPrimaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(client);
            await writer.WriteLineAsync("show-settings".AsMemory(), timeout.Token);
        }
        catch
        {
            // A second instance still exits cleanly if the notification cannot arrive.
        }
    }

    private static async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(cancellationToken);
                if (command == "show-settings")
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => ((App)System.Windows.Application.Current).ShowSettings());
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _dataLock?.Dispose();
        if (_mutex is null)
            return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
    }
}
