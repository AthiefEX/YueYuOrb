using FloatDesk.Core.Models.DesktopAutomation;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVEnvironmentManagerTests
{
    [Fact]
    public async Task ResourceProfileMustBelongToRequestedEnvironment()
    {
        var manager = new PowerShellHyperVEnvironmentManager();
        var profile = new VmResourceProfile(Guid.NewGuid(), 4, 4096, 2048, 8192, 80, "D:\\VMs", "Default Switch");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(Guid.NewGuid(), Guid.NewGuid(), profile, default));

        Assert.Equal("HyperVResourceEnvironmentMismatch", error.Message);
    }
}
