using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Collections.Concurrent;

namespace FloatDesk.Windows.Tests;

public sealed class DesktopSessionControlTests
{
    [Fact(Timeout = 15_000)]
    public async Task ClientAndServerRoundTripListRunCancel()
    {
        var handler = new RecordingHandler();
        await using var server = new DesktopSessionControlServer(handler);
        server.Start();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        var listed = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.ListOperation,
                null,
                null,
                null),
            timeout.Token);
        var run = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.RunOperation,
                RecordingHandler.EnvironmentId,
                RecordingHandler.TaskId,
                null),
            timeout.Token);
        var cancelled = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.CancelOperation,
                null,
                null,
                RecordingHandler.RunId),
            timeout.Token);

        Assert.True(listed.Success);
        Assert.Single(listed.Environments!);
        Assert.Single(listed.Tasks!);
        Assert.True(run.Success);
        Assert.Equal(RecordingHandler.RunId, run.RunId);
        Assert.True(cancelled.Success);
        Assert.Equal(
            [
                DesktopSessionControlProtocol.ListOperation,
                DesktopSessionControlProtocol.RunOperation,
                DesktopSessionControlProtocol.CancelOperation
            ],
            handler.Requests.Select(request => request.Operation));
    }

    [Fact(Timeout = 15_000)]
    public async Task ServerRejectsUnknownProtocolVersion()
    {
        await using var server = new DesktopSessionControlServer(
            new RecordingHandler());
        server.Start();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        var response = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion + 1,
                DesktopSessionControlProtocol.ListOperation,
                null,
                null,
                null),
            timeout.Token);

        Assert.False(response.Success);
        Assert.Equal("InvalidRequest", response.ErrorCode);
    }

    [Fact(Timeout = 15_000)]
    public async Task HandlerFailureReturnsErrorAndKeepsServerAlive()
    {
        await using var server = new DesktopSessionControlServer(
            new ThrowingHandler());
        server.Start();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));

        var failed = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.ListOperation,
                null,
                null,
                null),
            timeout.Token);
        var recovered = await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                DesktopSessionControlProtocol.ListOperation,
                null,
                null,
                null),
            timeout.Token);

        Assert.False(failed.Success);
        Assert.Equal("HandlerFailed", failed.ErrorCode);
        Assert.True(recovered.Success);
    }

    private sealed class RecordingHandler :
        IDesktopSessionControlHandler
    {
        public static readonly Guid EnvironmentId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid TaskId =
            Guid.Parse("22222222-2222-2222-2222-222222222222");
        public static readonly Guid RunId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");

        public ConcurrentQueue<DesktopSessionControlRequest> Requests
        { get; } = [];

        public Task<DesktopSessionControlResponse> HandleAsync(
            DesktopSessionControlRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            DesktopSessionControlResponse response = request.Operation switch
            {
                DesktopSessionControlProtocol.ListOperation =>
                    new DesktopSessionControlResponse(
                        true,
                        null,
                        null,
                        [
                            new DesktopSessionControlEnvironment(
                                EnvironmentId,
                                "Test environment",
                                "LocalWts")
                        ],
                        [
                            new DesktopSessionControlTask(
                                TaskId,
                                "Test task",
                                EnvironmentId,
                                true)
                        ],
                        null,
                        null,
                        null,
                        null),
                DesktopSessionControlProtocol.RunOperation =>
                    new DesktopSessionControlResponse(
                        true,
                        null,
                        null,
                        null,
                        null,
                        RunId,
                        "Succeeded",
                        1,
                        null),
                DesktopSessionControlProtocol.CancelOperation =>
                    new DesktopSessionControlResponse(
                        true,
                        null,
                        null,
                        null,
                        null,
                        RunId,
                        "Cancelled",
                        0,
                        null),
                _ => DesktopSessionControlResponse.Fail(
                    "UnsupportedOperation",
                    "Unsupported operation.")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler :
        IDesktopSessionControlHandler
    {
        private int _calls;

        public Task<DesktopSessionControlResponse> HandleAsync(
            DesktopSessionControlRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("handler failed");
            return Task.FromResult(
                new DesktopSessionControlResponse(
                    true,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null));
        }
    }
}
