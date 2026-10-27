namespace FloatDesk.App.ViewModels;

internal sealed class LauncherDropOperationGate
{
    private long _version;
    private LauncherDropOperation? _active;

    public bool IsActive => _active is not null;

    public LauncherDropOperation? TryStart(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_active is not null)
            return null;

        var operation = new LauncherDropOperation(
            ++_version,
            owner,
            new CancellationTokenSource());
        _active = operation;
        return operation;
    }

    public bool IsCurrent(
        LauncherDropOperation? operation,
        object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return operation is not null &&
               ReferenceEquals(_active, operation) &&
               operation.Version == _version &&
               ReferenceEquals(operation.Owner, owner) &&
               !operation.Cancellation.IsCancellationRequested;
    }

    public void Complete(LauncherDropOperation? operation)
    {
        if (operation is null)
            return;
        if (ReferenceEquals(_active, operation))
            _active = null;
        operation.Cancellation.Dispose();
    }

    public void Cancel()
    {
        var active = _active;
        _active = null;
        _version++;
        active?.Cancellation.Cancel();
    }
}

internal sealed record LauncherDropOperation(
    long Version,
    object Owner,
    CancellationTokenSource Cancellation);
