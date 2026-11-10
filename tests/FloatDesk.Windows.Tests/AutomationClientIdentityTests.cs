using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Diagnostics;

namespace FloatDesk.Windows.Tests;

public sealed class AutomationClientIdentityTests
{
    [Fact]
    public void NativeIdentityBindsRealLauncherAndRejectsReusedProcessIdentity()
    {
        using var process=Process.GetCurrentProcess();
        var caller=new AutomationCaller((uint)process.Id,(uint)process.SessionId,process.StartTime.ToUniversalTime());
        var first=AutomationClientIdentity.Resolve(caller,"Display name");
        var second=AutomationClientIdentity.Resolve(caller,"Other self-reported name");
        Assert.NotEqual(first.Id,second.Id);Assert.Equal(first.Sha256,second.Sha256);Assert.Equal(64,first.Sha256.Length);Assert.True(File.Exists(first.ExecutablePath));
        Assert.Throws<InvalidOperationException>(()=>AutomationClientIdentity.Resolve(caller with {StartedAtUtc=caller.StartedAtUtc.AddSeconds(1)},"forged"));
    }
}
