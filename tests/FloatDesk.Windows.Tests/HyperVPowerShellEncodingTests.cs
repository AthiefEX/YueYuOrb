using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVPowerShellEncodingTests
{
    [Fact]
    public async Task ManagementProcessPreservesChineseVmNames()
    {
        var host = new PowerShellHyperVHost(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"));
        var output = await host.RunScriptForManagementAsyncCore("'月语自动分身 · 测试'", CancellationToken.None);
        Assert.Equal("月语自动分身 · 测试", output);
    }
}
