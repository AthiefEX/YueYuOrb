using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVConsoleScriptTests
{
    private static readonly Guid VmId = Guid.Parse("8f10b015-1df9-4682-8a18-a32d76d9f119");

    [Fact]
    public void SaveStateScriptRequestsASaveAndVerifiesTheState()
    {
        var script = HyperVConsoleSettings.BuildCloseScript(VmId, HyperVConsoleCloseAction.SaveState);

        // Stop-VM without -Save would turn the guest off and lose its memory; the readback is what
        // keeps a host that silently ignores the request from being reported as success.
        Assert.Contains("Stop-VM -VM $vm -Save", script, StringComparison.Ordinal);
        Assert.Contains("VMStateReadbackMismatch", script, StringComparison.Ordinal);
        Assert.Contains("$after -ne 'Saved'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-TurnOff", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownScriptTurnsOffAndVerifiesTheState()
    {
        var script = HyperVConsoleSettings.BuildCloseScript(VmId, HyperVConsoleCloseAction.Shutdown);

        Assert.Contains("Stop-VM -VM $vm -TurnOff", script, StringComparison.Ordinal);
        Assert.Contains("$after -ne 'Off'", script, StringComparison.Ordinal);
        Assert.Contains("VMStateReadbackMismatch", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CloseViewScriptNeverTouchesTheVirtualMachine()
    {
        var script = HyperVConsoleSettings.BuildCloseScript(VmId, HyperVConsoleCloseAction.CloseView);

        Assert.Equal("'ok'", script);
        Assert.DoesNotContain("Stop-VM", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyIdentityIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            HyperVConsoleSettings.BuildCloseScript(Guid.Empty, HyperVConsoleCloseAction.SaveState));
    }
}
