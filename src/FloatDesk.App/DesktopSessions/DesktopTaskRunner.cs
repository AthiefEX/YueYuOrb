using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Globalization;

namespace FloatDesk.App.DesktopSessions;

public sealed class DesktopTaskRunner
{
    private const int MaximumSteps = 200;
    private readonly IDesktopSessionTransport _transport;
    private readonly IDesktopTaskResolutionProvider _resolutionProvider;
    private readonly IClock _clock;
    private readonly IDesktopTaskRunObserver? _observer;
    private readonly Func<Guid> _runIdFactory;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ActiveRun> _active = [];

    public DesktopTaskRunner(
        IDesktopSessionTransport transport,
        IDesktopTaskResolutionProvider resolutionProvider,
        IClock clock,
        IDesktopTaskRunObserver? observer = null,
        Func<Guid>? runIdFactory = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport ??
            throw new ArgumentNullException(nameof(transport));
        _resolutionProvider = resolutionProvider ??
            throw new ArgumentNullException(nameof(resolutionProvider));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _observer = observer;
        _runIdFactory = runIdFactory ?? (() => Guid.NewGuid());
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<DesktopTaskRunResult> RunAsync(
        SessionHandle handle,
        DesktopSessionTask task,
        CancellationToken cancellationToken) =>
        RunAsync(
            handle,
            task,
            _runIdFactory(),
            cancellationToken);

    public async Task<DesktopTaskRunResult> RunAsync(
        SessionHandle handle,
        DesktopSessionTask task,
        Guid runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(task);
        Validate(task);
        if (task.EnvironmentId != handle.EnvironmentId)
        {
            throw new InvalidOperationException(
                "The task belongs to a different desktop environment.");
        }

        using var runCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var active = new ActiveRun(handle, runCancellation);
        lock (_sync)
        {
            if (!_active.TryAdd(runId, active))
            {
                throw new InvalidOperationException(
                    "A desktop task run with the same ID is already active.");
            }
        }

        var completed = 0;
        try
        {
            for (var index = 0; index < task.Steps.Count; index++)
            {
                var step = task.Steps[index];
                runCancellation.Token.ThrowIfCancellationRequested();
                var validation = await ValidateCoordinatesAsync(
                    handle,
                    step,
                    runCancellation.Token).ConfigureAwait(false);
                if (validation is not null)
                {
                    Report(
                        runId,
                        step,
                        index,
                        DesktopTaskRunPhase.StepFailed);
                    return Failed(
                        runId,
                        task,
                        completed,
                        step,
                        validation);
                }

                Report(
                    runId,
                    step,
                    index,
                    DesktopTaskRunPhase.StepStarted);
                var result = await ExecuteStepAsync(
                    handle,
                    step,
                    runId,
                    active,
                    runCancellation.Token).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    completed++;
                    Report(
                        runId,
                        step,
                        index,
                        DesktopTaskRunPhase.StepSucceeded);
                    continue;
                }

                if (result.OutcomeUnknown)
                {
                    Report(
                        runId,
                        step,
                        index,
                        DesktopTaskRunPhase.StepFailed);
                    return new DesktopTaskRunResult(
                        runId,
                        task.Id,
                        DesktopTaskRunOutcome.UnknownOutcome,
                        completed,
                        step.Id,
                        result.ErrorCode ?? "OutcomeUnknown");
                }

                Report(
                    runId,
                    step,
                    index,
                    DesktopTaskRunPhase.StepFailed);
                return Failed(
                    runId,
                    task,
                    completed,
                    step,
                    result.ErrorCode ?? "DesktopTaskStepFailed");
            }

            return new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Succeeded,
                completed,
                FailedStepId: null,
                ErrorCode: null);
        }
        catch (OperationCanceledException)
            when (runCancellation.IsCancellationRequested)
        {
            return new DesktopTaskRunResult(
                runId,
                task.Id,
                DesktopTaskRunOutcome.Cancelled,
                completed,
                FailedStepId: null,
                ErrorCode: "Cancelled");
        }
        finally
        {
            lock (_sync)
                _active.Remove(runId);
        }
    }

    public async Task<bool> CancelRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        ActiveRun? active;
        Guid? commandId;
        lock (_sync)
        {
            if (!_active.TryGetValue(runId, out active))
                return false;
            commandId = active.CommandId;
        }

        try
        {
            if (commandId is { } current)
            {
                await _transport.CancelAsync(
                    active.Handle,
                    current,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                active.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return true;
    }

    public static void Validate(DesktopSessionTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.FormatVersion !=
                DesktopSessionFormat.CurrentTaskFormatVersion ||
            task.Id == Guid.Empty ||
            task.EnvironmentId == Guid.Empty ||
            task.Revision < 1 ||
            task.Steps is null ||
            task.Steps.Count > MaximumSteps ||
            task.Steps.Any(step =>
                step is null ||
                step.Id == Guid.Empty ||
                !Enum.IsDefined(step.Kind) ||
                step.Parameters is null ||
                step.TimeoutSeconds is < 1 or > 600 ||
                step.RetryCount is < 0 or > 3))
        {
            throw new InvalidOperationException(
                "The desktop task format is invalid or exceeds 200 steps.");
        }

        if (task.Steps
            .GroupBy(step => step.Id)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException(
                "Desktop task step IDs must be unique.");
        }
    }

    private async Task<CommandResult> ExecuteStepAsync(
        SessionHandle handle,
        SessionTaskStep step,
        Guid runId,
        ActiveRun active,
        CancellationToken cancellationToken)
    {
        var attempts = step.Kind == SessionCommandKind.WaitForWindow
            ? Math.Min(step.RetryCount, 3) + 1
            : 1;
        CommandResult? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = new SessionCommand(
                Guid.NewGuid(),
                handle,
                step.Kind,
                step.Parameters,
                _timeProvider.GetUtcNow().AddSeconds(
                    step.TimeoutSeconds));
            using var stepTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(step.TimeoutSeconds),
                _timeProvider);
            using var attemptCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    stepTimeout.Token);
            lock (_sync)
                active.CommandId = command.CommandId;
            try
            {
                last = await _transport.SendAsync(
                        handle,
                        command,
                        attemptCancellation.Token)
                    .WaitAsync(attemptCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      stepTimeout.IsCancellationRequested)
            {
                try
                {
                    await _transport.CancelAsync(
                        handle,
                        command.CommandId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }

                return new CommandResult(
                    command.CommandId,
                    false,
                    true,
                    "StepTimedOut");
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or
                UnauthorizedAccessException)
            {
                return new CommandResult(
                    command.CommandId,
                    false,
                    true,
                    "TransportFailure");
            }
            catch (InvalidOperationException)
            {
                return new CommandResult(
                    command.CommandId,
                    false,
                    false,
                    "TransportUnavailable");
            }
            finally
            {
                lock (_sync)
                    active.CommandId = null;
            }

            if (last.Succeeded ||
                last.OutcomeUnknown ||
                step.Kind != SessionCommandKind.WaitForWindow ||
                attempt + 1 >= attempts)
            {
                return last;
            }

            await _clock.DelayAsync(
                TimeSpan.FromMilliseconds(250),
                cancellationToken).ConfigureAwait(false);
        }

        return last ?? new CommandResult(
            Guid.Empty,
            false,
            false,
            "StepNotExecuted");
    }

    private async Task<string?> ValidateCoordinatesAsync(
        SessionHandle handle,
        SessionTaskStep step,
        CancellationToken cancellationToken)
    {
        if (step.Kind != SessionCommandKind.ClickElement ||
            !IsCoordinateClick(step.Parameters))
        {
            return null;
        }

        if (!TryGetPositiveInt(step.Parameters, "x", out _) ||
            !TryGetPositiveInt(step.Parameters, "y", out _) ||
            !TryGetPositiveInt(
                step.Parameters,
                "expectedWidth",
                out var expectedWidth) ||
            !TryGetPositiveInt(
                step.Parameters,
                "expectedHeight",
                out var expectedHeight))
        {
            return "InvalidCoordinates";
        }

        var resolution = await _resolutionProvider.GetResolutionAsync(
            handle,
            cancellationToken).ConfigureAwait(false);
        return resolution.Width == expectedWidth &&
            resolution.Height == expectedHeight
            ? null
            : "ResolutionChanged";
    }

    private static bool IsCoordinateClick(
        IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("strategy", out var strategy) &&
            string.Equals(
                strategy,
                "coordinates",
                StringComparison.OrdinalIgnoreCase) ||
        parameters.ContainsKey("x") ||
        parameters.ContainsKey("y");

    private static bool TryGetPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out int value)
    {
        value = 0;
        return parameters.TryGetValue(name, out var text) &&
            int.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value) &&
            value >= 0;
    }

    private void Report(
        Guid runId,
        SessionTaskStep step,
        int index,
        DesktopTaskRunPhase phase)
    {
        _observer?.Report(new DesktopTaskRunEvent(
            runId,
            step.Id,
            index,
            step.Kind,
            phase));
    }

    private static DesktopTaskRunResult Failed(
        Guid runId,
        DesktopSessionTask task,
        int completed,
        SessionTaskStep step,
        string errorCode) =>
        new(
            runId,
            task.Id,
            DesktopTaskRunOutcome.Failed,
            completed,
            step.Id,
            errorCode);

    private sealed class ActiveRun(
        SessionHandle handle,
        CancellationTokenSource cancellation)
    {
        public SessionHandle Handle { get; } = handle;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Guid? CommandId { get; set; }
    }
}
