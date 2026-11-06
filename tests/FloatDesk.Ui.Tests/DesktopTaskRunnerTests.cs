using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopTaskRunnerTests
{
    private static readonly Guid RunId =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task ClickNotRetriedAfterLostAck()
    {
        var transport = new RecordingTransport
        {
            ResultFactory = (_, call) => call == 1
                ? new CommandResult(Guid.Empty, false, true, "OutcomeUnknown")
                : new CommandResult(Guid.Empty, true, false, null)
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.ClickElement,
                RetryCount: 3,
                ("automationId", "submit")));

        var result = await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.Equal(DesktopTaskRunOutcome.UnknownOutcome, result.Outcome);
        Assert.Equal(1, transport.SendCount);
    }

    [Fact]
    public async Task WaitRetriesAtMostThreeTimes()
    {
        var transport = new RecordingTransport
        {
            ResultFactory = (command, _) =>
                new CommandResult(
                    command.CommandId,
                    false,
                    false,
                    "WindowNotFound")
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.WaitForWindow,
                RetryCount: 3,
                ("title", "Welcome")));

        var result = await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.Equal(DesktopTaskRunOutcome.Failed, result.Outcome);
        Assert.Equal(4, transport.SendCount);
    }

    [Fact]
    public async Task CancelKillsOnlyOwnedScriptTree()
    {
        var transport = new RecordingTransport
        {
            BlockCancellation = true
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.RunScript,
                RetryCount: 0,
                ("scriptPath", @"C:\tools\owned.ps1")));
        transport.ActiveRunId = RunId;
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunAsync(
            Handle,
            task,
            cancellation.Token);
        await transport.FirstSendEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        await runner.CancelRunAsync(
            transport.ActiveRunId,
            CancellationToken.None);
        cancellation.Cancel();
        var result = await run;

        Assert.Equal(DesktopTaskRunOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, transport.CancelCount);
    }

    [Fact]
    public async Task CancelFailureStillCancelsRunLocally()
    {
        var transport = new RecordingTransport
        {
            BlockCancellation = true,
            CancelException = new IOException("transport unavailable")
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.RunScript,
                RetryCount: 0,
                ("scriptPath", @"C:\tools\owned.ps1")));
        var run = runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);
        await transport.FirstSendEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<IOException>(
            () => runner.CancelRunAsync(
                RunId,
                CancellationToken.None));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DesktopTaskRunOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, transport.CancelCount);
    }

    [Fact]
    public async Task CoordinatesRejectChangedResolution()
    {
        var transport = new RecordingTransport();
        var resolution = new FixedResolutionProvider(1920, 1080);
        var runner = CreateRunner(transport, resolution);
        var task = CreateTask(
            Step(
                SessionCommandKind.ClickElement,
                RetryCount: 0,
                ("strategy", "coordinates"),
                ("x", "100"),
                ("y", "200"),
                ("expectedWidth", "2560"),
                ("expectedHeight", "1440")));

        var result = await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.Equal(DesktopTaskRunOutcome.Failed, result.Outcome);
        Assert.Equal("ResolutionChanged", result.ErrorCode);
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task StepTimeoutCancelsRemoteCommand()
    {
        var transport = new RecordingTransport
        {
            BlockCancellation = true
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.RunScript,
                RetryCount: 0,
                TimeoutSeconds: 1,
                ("scriptPath", @"C:\tools\owned.ps1")));

        var result = await runner.RunAsync(
                Handle,
                task,
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(DesktopTaskRunOutcome.UnknownOutcome, result.Outcome);
        Assert.Equal("StepTimedOut", result.ErrorCode);
        Assert.Equal(1, transport.CancelCount);
    }

    [Fact]
    public async Task TransportFailureReturnsUnknownOutcome()
    {
        var transport = new RecordingTransport
        {
            SendException = new IOException("transport failed")
        };
        var runner = CreateRunner(transport);
        var task = CreateTask(
            Step(
                SessionCommandKind.ClickElement,
                RetryCount: 0,
                ("automationId", "submit")));

        var result = await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.Equal(DesktopTaskRunOutcome.UnknownOutcome, result.Outcome);
        Assert.Equal("TransportFailure", result.ErrorCode);
    }

    [Fact]
    public async Task PasswordsNotLogged()
    {
        var secret = "correct-horse-battery-staple";
        var observer = new RecordingObserver();
        var runner = CreateRunner(
            new RecordingTransport(),
            observer: observer);
        var task = CreateTask(
            Step(
                SessionCommandKind.TypeText,
                RetryCount: 0,
                ("text", secret)));

        await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.DoesNotContain(
            secret,
            string.Join("|", observer.Events));
    }

    [Fact]
    public async Task HostInputApiNotCalled()
    {
        var hostInput = new HostInputSpy();
        var runner = CreateRunner(new RecordingTransport());
        var task = CreateTask(
            Step(
                SessionCommandKind.SendKeys,
                RetryCount: 0,
                ("keys", "{ENTER}")));

        await runner.RunAsync(
            Handle,
            task,
            CancellationToken.None);

        Assert.Equal(0, hostInput.CallCount);
    }

    [Fact]
    public void TwoHundredStepsAcceptedTwoHundredOneRejected()
    {
        var runner = CreateRunner(new RecordingTransport());
        var accepted = CreateTask(
            Enumerable.Range(0, 200)
                .Select(index => Step(
                    SessionCommandKind.Delay,
                    RetryCount: 0,
                    ("milliseconds", "1")))
                .ToArray());
        var rejected = CreateTask(
            Enumerable.Range(0, 201)
                .Select(index => Step(
                    SessionCommandKind.Delay,
                    RetryCount: 0,
                    ("milliseconds", "1")))
                .ToArray());

        DesktopTaskRunner.Validate(accepted);
        Assert.Throws<InvalidOperationException>(
            () => DesktopTaskRunner.Validate(rejected));
    }

    private static DesktopTaskRunner CreateRunner(
        RecordingTransport transport,
        IDesktopTaskResolutionProvider? resolution = null,
        IDesktopTaskRunObserver? observer = null) =>
        new(
            transport,
            resolution ?? new FixedResolutionProvider(2560, 1440),
            new ImmediateClock(),
            observer,
            () => RunId);

    private static SessionHandle Handle { get; } = new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        WindowsSessionId: 3,
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        PreparationCompleted: true);

    private static DesktopSessionTask CreateTask(
        params SessionTaskStep[] steps) =>
        new(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "测试任务",
            Handle.EnvironmentId,
            steps,
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            Revision: 1,
            Enabled: true);

    private static SessionTaskStep Step(
        SessionCommandKind kind,
        int RetryCount,
        params (string Key, string Value)[] parameters) =>
        Step(
            kind,
            RetryCount,
            TimeoutSeconds: 5,
            parameters);

    private static SessionTaskStep Step(
        SessionCommandKind kind,
        int RetryCount,
        int TimeoutSeconds,
        params (string Key, string Value)[] parameters) =>
        new(
            Guid.NewGuid(),
            kind,
            parameters.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            TimeoutSeconds,
            RetryCount);

    private sealed class RecordingTransport : IDesktopSessionTransport
    {
        private readonly object _sync = new();
        private SessionHandle? _activeHandle;
        private Guid _activeCommandId;
        private int _sendCount;

        public Func<SessionCommand, int, CommandResult> ResultFactory
        { get; set; } =
            (command, _) =>
                new CommandResult(command.CommandId, true, false, null);
        public bool BlockCancellation { get; set; }
        public Exception? CancelException { get; set; }
        public Exception? SendException { get; set; }
        public TaskCompletionSource FirstSendEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int SendCount => Volatile.Read(ref _sendCount);
        public int CancelCount { get; private set; }
        public Guid ActiveRunId { get; set; }

        public Task<SessionTransportHandshake> ConnectAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionTransportHandshake(
                true,
                handle.Generation,
                handle.LoginInstanceId,
                null));

        public Task AcquireInputLeaseAsync(
            SessionHandle handle,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RenewInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReleaseInputLeaseAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CancelAsync(
            SessionHandle handle,
            Guid commandId,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                Assert.Equal(_activeHandle, handle);
                Assert.Equal(_activeCommandId, commandId);
                CancelCount++;
            }

            return CancelException is null
                ? Task.CompletedTask
                : Task.FromException(CancelException);
        }

        public async Task<CommandResult> SendAsync(
            SessionHandle handle,
            SessionCommand command,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _sendCount);
            lock (_sync)
            {
                _activeHandle = handle;
                _activeCommandId = command.CommandId;
            }

            FirstSendEntered.TrySetResult();
            if (SendException is not null)
                throw SendException;
            if (BlockCancellation)
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }

            return ResultFactory(command, call);
        }
    }

    private sealed record FixedResolutionProvider(
        int Width,
        int Height) : IDesktopTaskResolutionProvider
    {
        public Task<DesktopSessionResolution> GetResolutionAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopSessionResolution(Width, Height));
    }

    private sealed class RecordingObserver : IDesktopTaskRunObserver
    {
        public List<string> Events { get; } = [];

        public void Report(DesktopTaskRunEvent value) =>
            Events.Add(
                $"{value.Phase}:{value.StepId}:{value.Kind}:{value.StepIndex}");
    }

    private sealed class HostInputSpy
    {
        public int CallCount { get; private set; }

        public void Send() => CallCount++;
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
