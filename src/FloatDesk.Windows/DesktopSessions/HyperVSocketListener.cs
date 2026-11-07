using System.Net;
using System.Net.Sockets;

namespace FloatDesk.Windows.DesktopSessions;

public sealed class WindowsHyperVSocketListener : IAsyncDisposable
{
    private const int AddressFamilyHyperV = 34;
    private const int ProtocolRaw = 1;
    private readonly Socket _socket;
    private readonly Guid _serviceId;
    private bool _started;

    public WindowsHyperVSocketListener(Guid serviceId)
    {
        if (serviceId == Guid.Empty)
            throw new ArgumentException("A service identity is required.", nameof(serviceId));
        _serviceId = serviceId;
        _socket = new Socket((AddressFamily)AddressFamilyHyperV, SocketType.Stream, (ProtocolType)ProtocolRaw);
    }

    public Guid ServiceId => _serviceId;

    public void Start(int backlog = 1)
    {
        if (_started)
            throw new InvalidOperationException("The Hyper-V socket listener is already started.");
        if (backlog is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(backlog));
        _socket.Bind(new HyperVSocketEndPoint(Guid.Empty, _serviceId));
        _socket.Listen(backlog);
        _started = true;
    }

    public async Task AcceptLoopAsync(
        Func<Stream, CancellationToken, Task> connectionHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionHandler);
        if (!_started)
            throw new InvalidOperationException("The Hyper-V socket listener is not started.");
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            _ = HandleClientAsync(client, connectionHandler, cancellationToken);
        }
    }

    private static async Task HandleClientAsync(
        Socket client,
        Func<Stream, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);
        try { await handler(stream, cancellationToken).ConfigureAwait(false); }
        catch (IOException) { }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class HyperVSocketEndPoint(Guid vmId, Guid serviceId) : EndPoint
    {
        public override AddressFamily AddressFamily => (AddressFamily)AddressFamilyHyperV;

        public override SocketAddress Serialize()
        {
            var address = new SocketAddress(AddressFamily, 36);
            var buffer = address.Buffer.Span;
            buffer.Clear();
            buffer[0] = AddressFamilyHyperV;
            vmId.TryWriteBytes(buffer.Slice(4, 16));
            serviceId.TryWriteBytes(buffer.Slice(20, 16));
            return address;
        }

        public override EndPoint Create(SocketAddress socketAddress) => throw new NotSupportedException();
    }
}
