using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class LauncherDropOperationGateTests
{
    [Fact]
    public void RejectsConcurrentOperationAndAcceptsNextAfterCompletion()
    {
        var gate = new LauncherDropOperationGate();
        var owner = NewOwner();

        var first = gate.TryStart(owner);
        var concurrent = gate.TryStart(owner);

        Assert.NotNull(first);
        Assert.Null(concurrent);
        Assert.True(gate.IsCurrent(first, owner));

        gate.Complete(first);
        var next = gate.TryStart(owner);

        Assert.NotNull(next);
        Assert.True(gate.IsCurrent(next, owner));
    }

    [Fact]
    public void CancelInvalidatesInFlightResultAndAllowsNewOperation()
    {
        var gate = new LauncherDropOperationGate();
        var owner = NewOwner();
        var stale = Assert.IsType<LauncherDropOperation>(gate.TryStart(owner));

        gate.Cancel();
        var current = gate.TryStart(owner);

        Assert.True(stale.Cancellation.IsCancellationRequested);
        Assert.False(gate.IsCurrent(stale, owner));
        Assert.True(gate.IsCurrent(current, owner));

        gate.Complete(stale);
        Assert.True(gate.IsCurrent(current, owner));
    }

    [Fact]
    public void ChangedOwnerCannotReceiveInFlightResult()
    {
        var gate = new LauncherDropOperationGate();
        var oldOwner = NewOwner();
        var newOwner = NewOwner();
        var operation = gate.TryStart(oldOwner);

        Assert.False(gate.IsCurrent(operation, newOwner));
    }

    private static SettingsViewModel NewOwner() =>
        SettingsViewModel.From(AppSettings.CreateDefault());
}
