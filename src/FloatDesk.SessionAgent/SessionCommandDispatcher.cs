using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Collections.Concurrent;

namespace FloatDesk.SessionAgent;

public interface ISessionInputExecutor
{
    Task<CommandResult> ExecuteAsync(
        SessionCommand command,
        CancellationToken cancellationToken);

    Task ReleaseHeldInputAsync(CancellationToken cancellationToken);
}

public sealed class SessionCommandDispatcher
{
    private readonly SessionAgentBinding _binding;
    private readonly ISessionInputExecutor _executor;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Task<CommandResult>> _dispatches = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _active = new();
    private readonly HashSet<Guid> _released = new();

    public SessionCommandDispatcher(
        SessionAgentBinding binding,
        ISessionInputExecutor executor)
    {
        _binding = binding ??
            throw new ArgumentNullException(nameof(binding));
        _executor = executor ??
            throw new ArgumentNullException(nameof(executor));
    }

    public Task<CommandResult> DispatchAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            Validate(command);
        }
        catch (SessionCommandRejectedException exception)
        {
            return Task.FromResult(new CommandResult(
                command.CommandId,
                false,
                false,
                exception.Message));
        }

        lock (_sync)
        {
            if (_dispatches.TryGetValue(
                    command.CommandId,
                    out var existing))
            {
                return existing;
            }

            var dispatchCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            _active[command.CommandId] = dispatchCancellation;
            _released.Remove(command.CommandId);
            var dispatch = ExecuteAsync(
                command,
                dispatchCancellation);
            _dispatches[command.CommandId] = dispatch;
            return dispatch;
        }
    }

    public async Task CancelAsync(
        Guid commandId,
        CancellationToken cancellationToken)
    {
        bool shouldRelease;
        lock (_sync)
        {
            if (_active.TryGetValue(commandId, out var cancellation))
                cancellation.Cancel();
            shouldRelease = _released.Add(commandId);
        }

        if (shouldRelease)
        {
            await _executor.ReleaseHeldInputAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken)
    {
        Guid[] commandIds;
        lock (_sync)
        {
            commandIds = _active.Keys.ToArray();
        }

        foreach (var commandId in commandIds)
        {
            await CancelAsync(
                commandId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CommandResult> ExecuteAsync(
        SessionCommand command,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _executor.ExecuteAsync(
                command,
                cancellation.Token).ConfigureAwait(false);
            return result with { CommandId = command.CommandId };
        }
        catch (OperationCanceledException)
            when (cancellation.IsCancellationRequested)
        {
            return new CommandResult(
                command.CommandId,
                false,
                true,
                "OutcomeUnknown");
        }
        catch
        {
            return new CommandResult(
                command.CommandId,
                false,
                false,
                "InputExecutorFailed");
        }
        finally
        {
            lock (_sync)
            {
                _active.Remove(command.CommandId);
                cancellation.Dispose();
            }
        }
    }

    private void Validate(SessionCommand command)
    {
        if (command.Handle.EnvironmentId != _binding.EnvironmentId ||
            command.Handle.Generation != _binding.Generation ||
            command.Handle.WindowsSessionId != _binding.WindowsSessionId ||
            command.Handle.LoginInstanceId != _binding.LoginInstanceId)
        {
            if (command.Handle.WindowsSessionId != _binding.WindowsSessionId)
            {
                throw new SessionCommandRejectedException("WrongSession");
            }

            throw new SessionCommandRejectedException("StaleGeneration");
        }

        if (command.DeadlineUtc <= DateTimeOffset.UtcNow)
            throw new SessionCommandRejectedException("CommandExpired");
    }

    private sealed class SessionCommandRejectedException(string errorCode) :
        InvalidOperationException(errorCode);
}
