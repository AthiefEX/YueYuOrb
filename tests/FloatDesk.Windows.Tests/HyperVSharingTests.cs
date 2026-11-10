using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVSharingTests
{
    [Fact]
    public void RedirectsOnlyExplicitDrivesAndClearsPreviousSelection()
    {
        var script = HyperVConsoleSettings.BuildDisplayScript(Guid.NewGuid(), 1280, 720, ["F:\\", "Z:\\"]);
        Assert.Contains("$options.RedirectedDrives='f;z'", script);
        Assert.DoesNotContain("RedirectedDrives='*'", script);
        Assert.Contains("$options.RedirectedDrives=''", HyperVConsoleSettings.BuildDisplayScript(Guid.NewGuid(), 1280, 720));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("F:\\Games")]
    [InlineData("F:';Invoke-Expression 'bad")]
    public void RejectsNonRootRedirectionStrings(string path)
    {
        Assert.Throws<ArgumentException>(() => HyperVConsoleSettings.BuildDisplayScript(Guid.NewGuid(), 1280, 720, [path]));
    }

    [Fact]
    public void FolderMappingExposesFolderAndIsRemovedOnDispose()
    {
        var folder = Path.Combine(Path.GetTempPath(), "YueYuShareTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "probe.txt"), "probe");
            string drive;
            using (var lease = HyperVSharedDriveLease.Create([folder]))
            {
                drive = Assert.Single(lease.Drives);
                Assert.Equal("probe", File.ReadAllText(Path.Combine(drive, "probe.txt")));
                File.WriteAllText(Path.Combine(drive, "written.txt"), "written");
                Assert.Equal("written", File.ReadAllText(Path.Combine(folder, "written.txt")));
            }
            Assert.False(Directory.Exists(drive));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void FolderMappingIsVisibleToAChildProcessLikeVmConnect()
    {
        var folder = Path.Combine(Path.GetTempPath(), "YueYuShareChildTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "probe.txt"), "probe");
            using var lease = HyperVSharedDriveLease.Create([folder]);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Get-Content -LiteralPath '" + lease.Drives[0] + "probe.txt' -Raw"
                }
            })!;
            var output = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("probe", output.Trim());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void MissingFolderDoesNotLeaveEarlierMappingBehind()
    {
        var before = DriveInfo.GetDrives().Select(d => d.Name).ToArray();
        Assert.Throws<DirectoryNotFoundException>(() => HyperVSharedDriveLease.Create([Path.GetTempPath(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]));
        Assert.Equal(before, DriveInfo.GetDrives().Select(d => d.Name).ToArray());
    }
}
