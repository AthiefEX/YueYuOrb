using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVAutomaticInstallTests
{
    [Fact]
    public void PersistentLoginMustWaitForOneTimeLoginCleanup()
    {
        var script = HyperVAutomaticInstaller.Script;
        Assert.Contains("YueYuOrb\\Bootstrap", script);
        Assert.Contains("-Name Completed -Value 1", script);
        Assert.True(script.IndexOf("-Name DefaultPassword -ErrorAction SilentlyContinue", StringComparison.Ordinal) <
            script.IndexOf("-Name Completed -Value 1", StringComparison.Ordinal));
        Assert.Contains("GuestBootstrapState:", HyperVGuestAdministration.Script);
    }
    [Fact]
    public void OfflineInstallOnlyPartitionsTheDiskReturnedByItsOwnVhdMount()
    {
        var script = HyperVAutomaticInstaller.Script;
        Assert.Contains("Mount-VHD -Path $vhd -NoDriveLetter -Passthru", script);
        Assert.Contains("Initialize-Disk -Number $disk.Number", script);
        Assert.DoesNotContain("Clear-Disk", script);
        Assert.Contains("/Apply-Image", script);
        Assert.Contains("bcdboot.exe", script);
        Assert.Contains("finally", script);
        Assert.Contains("Dismount-VHD", script);
        Assert.Contains("[Console]::In.ReadToEnd()", script);
        Assert.Contains("FirstBootDevice", script);
    }

    [Fact]
    public void FormatsVirtualPartitionsBeforeExposingDriveLettersToHostShell()
    {
        var script = HyperVAutomaticInstaller.Script;
        Assert.DoesNotContain("-GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' -AssignDriveLetter", script);
        Assert.DoesNotContain("-UseMaximumSize -AssignDriveLetter", script);
        Assert.True(script.IndexOf("$efi | Format-Volume", StringComparison.Ordinal) < script.IndexOf("$efi | Add-PartitionAccessPath", StringComparison.Ordinal));
        Assert.True(script.IndexOf("$os | Format-Volume", StringComparison.Ordinal) < script.IndexOf("$os | Add-PartitionAccessPath", StringComparison.Ordinal));
        Assert.Contains("-NoDriveLetter", script);
        Assert.DoesNotContain("$os | Add-PartitionAccessPath -AssignDriveLetter", script);
        Assert.Contains("PROGRESS:", script);
    }

    [Fact]
    public void UsesDirectoryMountedIsoInsteadOfVolumeGuidPathForDismCompatibility()
    {
        var script = HyperVAutomaticInstaller.Script;
        Assert.Contains("mountvol.exe", script);
        Assert.Contains("$sources = Join-Path $isoRoot 'sources'", script);
        Assert.Contains("$isoRoot /D", script);
        Assert.Contains("FAILURE:", script);
    }
}
