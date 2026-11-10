using System.Diagnostics;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVGpuSharingTests
{
    [Fact]
    public void EmptyIdentityIsRejectedBeforeExecution() =>
        Assert.Throws<ArgumentException>(() => HyperVGpuSharing.BuildScript(Guid.Empty, true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunningGuestIsRejectedBeforeAnyMutation(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"YueYuGpu-{Guid.NewGuid():N}.ps1");
        try
        {
            var script = HyperVGpuSharing.BuildScript(Guid.NewGuid(), enabled);
            // These overrides run the generated script through the real Windows PowerShell parser.
            await File.WriteAllTextAsync(path, """
                function Import-Module {}
                function Get-VM { [pscustomobject]@{State='Running'} }
                function Set-VM { throw 'Unexpected mutation' }
                function Mount-VHD { throw 'Unexpected mount' }
                try {
                """ + Environment.NewLine + script + Environment.NewLine + """
                } catch { [Console]::WriteLine($_.Exception.Message); exit 17 }
                """);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path }
            })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(17, process.ExitCode);
            Assert.Contains("Please shut down guest Windows", output);
            Assert.Empty(error);
        }
        finally { File.Delete(path); }
    }

}
