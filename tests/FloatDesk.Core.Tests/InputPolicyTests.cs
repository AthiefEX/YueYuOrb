using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class InputPolicyTests
{
    private static ForegroundSnapshot Target(string path, bool own = false) =>
        new(1, 10, 11, 42, 43, DateTimeOffset.UtcNow, path, own);

    [Fact]
    public void OwnWindowIsAlwaysExempt()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\FloatDesk\FloatDesk.exe", own: true),
            new InputContext(false, false, false, true, true),
            [Lock(@"C:\FloatDesk\FloatDesk.exe")]);

        Assert.Equal(InputAction.None, decision.Action);
    }

    [Fact]
    public void GlobalManualLockAppliesToExternalApplication()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Apps\game.exe"),
            new InputContext(false, false, false, true, false),
            []);

        Assert.Equal(InputAction.LockEnglish, decision.Action);
    }

    [Fact]
    public void RuleOnlyAppliesWhenItsApplicationIsForeground()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Apps\other.exe"),
            new InputContext(false, false, false, false, true),
            [Lock(@"C:\Apps\editor.exe")]);

        Assert.Equal(InputAction.None, decision.Action);
    }

    [Fact]
    public void ExactPathMatchingDoesNotConfuseSameFileName()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Other\editor.exe"),
            new InputContext(false, false, false, false, true),
            [Lock(@"C:\Apps\editor.exe")]);

        Assert.Equal(InputAction.None, decision.Action);
    }

    [Fact]
    public void ExclusionWinsOverManualLock()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Apps\chat.exe"),
            new InputContext(false, false, false, true, true),
            [Exclude(@"C:\Apps\chat.exe")]);

        Assert.Equal(InputAction.Exclude, decision.Action);
    }

    [Fact]
    public void CaptureLeasePausesAllInputRules()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Apps\editor.exe"),
            new InputContext(false, false, true, true, true),
            [Lock(@"C:\Apps\editor.exe")]);

        Assert.Equal(InputAction.None, decision.Action);
    }

    [Fact]
    public void OwnWindowPreservesExistingLockState()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\FloatDesk\FloatDesk.exe", own: true),
            new InputContext(false, false, false, true, true),
            []);

        Assert.True(decision.PreserveLockState);
    }

    [Fact]
    public void TemporaryPausesPreserveExistingLockState()
    {
        var snapshot = Target(@"C:\Apps\editor.exe");

        var emergency = InputPolicy.Decide(
            snapshot,
            new InputContext(false, true, false, true, true),
            []);
        var capture = InputPolicy.Decide(
            snapshot,
            new InputContext(false, false, true, true, true),
            []);
        var session = InputPolicy.Decide(
            snapshot,
            new InputContext(true, false, false, true, true),
            []);

        Assert.True(emergency.PreserveLockState);
        Assert.True(capture.PreserveLockState);
        Assert.True(session.PreserveLockState);
    }

    [Fact]
    public void ExclusionAndUnknownTargetPreservePendingRestore()
    {
        var excluded = InputPolicy.Decide(
            Target(@"C:\Apps\chat.exe"),
            new InputContext(false, false, false, true, true),
            [Exclude(@"C:\Apps\chat.exe")]);
        var unknown = InputPolicy.Decide(
            Target(""),
            new InputContext(false, false, false, true, true),
            []);

        Assert.True(excluded.PreserveLockState);
        Assert.True(unknown.PreserveLockState);
    }

    [Fact]
    public void OrdinaryNonMatchingApplicationAllowsFullRestore()
    {
        var decision = InputPolicy.Decide(
            Target(@"C:\Apps\other.exe"),
            new InputContext(false, false, false, false, true),
            [Lock(@"C:\Apps\editor.exe")]);

        Assert.False(decision.PreserveLockState);
    }

    private static AppRule Lock(string path) =>
        new(Guid.NewGuid(), path, InputAction.LockEnglish, true);

    private static AppRule Exclude(string path) =>
        new(Guid.NewGuid(), path, InputAction.Exclude, true);
}
