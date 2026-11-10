using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVGuestInstallerTests
{
    [Fact]
    public void ReinstallRepairsStartupEvenWhenPairingAlreadyExists()
    {
        var script = HyperVGuestInstaller.BuildInstallScript();
        var branch = script[script.IndexOf("if (-not (Test-Path -LiteralPath $bootstrap))", StringComparison.Ordinal)..];
        var earlyReturn = branch.IndexOf("return", StringComparison.Ordinal);
        Assert.True(branch.IndexOf("Set-ItemProperty -Path $run", StringComparison.Ordinal) < earlyReturn);
        Assert.Contains("Set-ItemProperty -Path $run", branch);
    }
    [Fact]
    public void InstallerVerifiesPayloadBeforeConfigurationAndRegistersGuestLogon()
    {
        var script = HyperVGuestInstaller.BuildInstallScript();
        Assert.True(script.IndexOf("Get-FileHash", StringComparison.Ordinal) < script.IndexOf("--hyperv-configure", StringComparison.Ordinal));
        Assert.Contains("RedirectStandardInput", script);
        Assert.Contains("--hyperv-guest", script);
        Assert.Contains("CurrentVersion\\Run", script);
        Assert.Contains("ExitCode", script);
        Assert.DoesNotContain("$config.Secret", script);
        Assert.Contains("Remove-Item -LiteralPath $bootstrap", script);
        Assert.Contains("continue", script);
        Assert.Contains("Agent already configured", script);
        Assert.Contains("$mutex.WaitOne(0)", script);
        Assert.Contains("[1/4]", script);
    }
}
