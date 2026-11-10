using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;
using FloatDesk.Windows.DesktopSessions;
using System.IO;
using System.IO.Pipes;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVTransportTests
{
    [Theory(Timeout = 10_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PairingTransportConnectsToGuestListener(bool acceptHostGeneration)
    {
        var vmId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var serviceId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var environmentId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        var generation =
            Guid.Parse("22222222-2222-2222-2222-222222222222");
        var loginInstance =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        var secret = Enumerable.Range(1, 32)
            .Select(value => (byte)value)
            .ToArray();
        using var outboundServer = new AnonymousPipeServerStream(
            PipeDirection.Out);
        using var outboundClient = new AnonymousPipeClientStream(
            PipeDirection.In,
            outboundServer.GetClientHandleAsString());
        using var inboundServer = new AnonymousPipeServerStream(
            PipeDirection.In);
        using var inboundClient = new AnonymousPipeClientStream(
            PipeDirection.Out,
            inboundServer.GetClientHandleAsString());
        await using var listenerStream = new DuplexStream(
            inboundServer,
            outboundServer);
        await using var clientStream = new DuplexStream(
            outboundClient,
            inboundClient);
        var binding = new SessionAgentBinding(
            environmentId,
            generation,
            loginInstance,
            WindowsSessionId: 4,
            UserSid: "S-1-5-21-1000");
        var listener = HyperVAgentListener.ServeAsync(
            listenerStream,
            new HyperVAgentListenerOptions(
                vmId,
                serviceId,
                acceptHostGeneration ? binding with { Generation = Guid.NewGuid() } : binding,
                secret.ToArray()) { AcceptHostGeneration = acceptHostGeneration },
            CancellationToken.None);

        var endpoints = new HyperVAgentEndpointRegistry();
        endpoints.Set(
            environmentId,
            new HyperVGuestIdentity(
                vmId,
                serviceId,
                binding.WindowsSessionId,
                binding.LoginInstanceId,
                BootId: "boot-1",
                binding.UserSid,
                DateTimeOffset.Parse(
                    "2026-11-06T01:02:03Z",
                    System.Globalization.CultureInfo.InvariantCulture)));
        await using var transport = new HyperVSocketTransport(
            new FixedConnector(clientStream),
            endpoints,
            new MemorySecretStore(secret));
        var handle = new SessionHandle(
            environmentId,
            generation,
            binding.WindowsSessionId,
            loginInstance,
            PreparationCompleted: true);

        var handshake = await transport.ConnectAsync(
            handle,
            CancellationToken.None);

        Assert.True(handshake.Succeeded);
        Assert.Equal(generation, handshake.Generation);
        Assert.Equal(loginInstance, handshake.LoginInstanceId);
        await transport.DisposeAsync();
        await listener.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class FixedConnector(Stream stream) :
        IHyperVSocketConnector
    {
        public Task<Stream> ConnectAsync(
            Guid vmId,
            Guid serviceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(stream);
    }

    private sealed class MemorySecretStore(byte[] secret) :
        IHyperVPairingSecretStore
    {
        public Task SaveAsync(
            Guid vmId,
            ReadOnlyMemory<byte> value,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<byte[]?> LoadAsync(
            Guid vmId,
            CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(secret.ToArray());

        public Task DeleteAsync(
            Guid vmId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class DuplexStream(
        Stream read,
        Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => write.Flush();

        public override Task FlushAsync(
            CancellationToken cancellationToken) =>
            write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            read.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            write.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                read.Dispose();
                write.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() =>
            base.DisposeAsync();
    }
}
