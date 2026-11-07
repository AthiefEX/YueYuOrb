using FloatDesk.Core.Models.DesktopAutomation;
using FloatDesk.SessionAgent;

namespace FloatDesk.Windows.Tests;

public sealed class GuestOperationDispatcherTests
{
    [Fact]
    public async Task RejectsRelativeApplicationPath()
    {
        var executor = new RecordingExecutor();
        var result = await new GuestOperationDispatcher(executor)
            .DispatchAsync(GuestOperation.StartApplication("demo.exe"), default);

        Assert.False(result.Succeeded);
        Assert.Equal("GuestApplicationPathInvalid", result.ErrorCode);
        Assert.Null(executor.StartedPath);
    }

    [Fact]
    public async Task DispatchesEnvironmentChangeToGuestExecutor()
    {
        var executor = new RecordingExecutor();
        var result = await new GuestOperationDispatcher(executor)
            .DispatchAsync(GuestOperation.SetEnvironmentVariable("DEMO", "1"), default);

        Assert.True(result.Succeeded);
        Assert.Equal(("DEMO", "1"), executor.Environment);
    }

    private sealed class RecordingExecutor : IGuestOperationExecutor
    {
        public string? StartedPath { get; private set; }
        public (string Name, string Value)? Environment { get; private set; }
        public Task<GuestOperationResult> StartApplicationAsync(string path, CancellationToken cancellationToken)
        { StartedPath = path; return Task.FromResult(new GuestOperationResult(true)); }
        public Task<GuestOperationResult> StopApplicationAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new GuestOperationResult(true));
        public Task<GuestOperationResult> SetEnvironmentVariableAsync(string name, string value, CancellationToken cancellationToken)
        { Environment = (name, value); return Task.FromResult(new GuestOperationResult(true)); }
    }
}
