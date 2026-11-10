using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.SessionAgent;

public interface ISessionAutomationBackend
{
    Task<CommandResult> WaitForWindowAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task<CommandResult> ClickElementAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task<CommandResult> TypeTextAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task<CommandResult> SendKeysAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task<CommandResult> CaptureScreenshotAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task ReleaseHeldInputAsync(CancellationToken cancellationToken);
}

public interface ISessionScriptRunner
{
    Task<CommandResult> RunAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task ReleaseOwnedProcessesAsync(
        CancellationToken cancellationToken);
}

public sealed class AutomationStepExecutor : ISessionInputExecutor
{
    private readonly ISessionAutomationBackend _automation;
    private readonly ISessionScriptRunner _scripts;
    private readonly IClock _clock;

    public AutomationStepExecutor(
        ISessionAutomationBackend automation,
        ISessionScriptRunner scripts,
        IClock clock)
    {
        _automation = automation ??
            throw new ArgumentNullException(nameof(automation));
        _scripts = scripts ??
            throw new ArgumentNullException(nameof(scripts));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Task<CommandResult> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Kind switch
        {
            SessionCommandKind.CloseApplication => SessionDesktopCommands.CloseAsync(command, cancellationToken),
            SessionCommandKind.ObserveDesktop => SessionDesktopCommands.ObserveAsync(command, cancellationToken),
            SessionCommandKind.LaunchApplication =>
                StartupApplicationRunner.RunAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.WaitForWindow =>
                _automation.WaitForWindowAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.ClickElement =>
                _automation.ClickElementAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.TypeText =>
                _automation.TypeTextAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.SendKeys =>
                _automation.SendKeysAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.Delay =>
                DelayAsync(command, cancellationToken),
            SessionCommandKind.CaptureScreenshot =>
                _automation.CaptureScreenshotAsync(
                    command,
                    cancellationToken),
            SessionCommandKind.RunScript =>
                _scripts.RunAsync(command, cancellationToken),
            _ => Task.FromResult(Failed(
                command,
                "UnsupportedCommandKind"))
        };
    }

    public async Task ReleaseHeldInputAsync(
        CancellationToken cancellationToken)
    {
        var failures = new List<Exception>(2);
        try
        {
            await _automation.ReleaseHeldInputAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await _scripts.ReleaseOwnedProcessesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(failures[0])
                .Throw();
        if (failures.Count > 1)
            throw new AggregateException(failures);
    }

    private async Task<CommandResult> DelayAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Parameters.TryGetValue(
                "milliseconds",
                out var millisecondsText) ||
            !int.TryParse(millisecondsText, out var milliseconds) ||
            milliseconds is < 0 or > 600_000)
        {
            return Failed(command, "InvalidDelay");
        }

        await _clock.DelayAsync(
            TimeSpan.FromMilliseconds(milliseconds),
            cancellationToken).ConfigureAwait(false);
        return new CommandResult(
            command.CommandId,
            true,
            false,
            null);
    }

    private static CommandResult Failed(
        SessionCommand command,
        string errorCode) =>
        new(command.CommandId, false, false, errorCode);
}
