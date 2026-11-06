using System.Diagnostics;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsWindowLifecycleTests
{
    [Fact]
    public async Task ProbeOpensAndReopensDefaultAndDesktopSessionSettings()
    {
        var probe = FindProbe();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = probe,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(
                "Settings-window probe timed out.");
        }

        Assert.True(
            process.ExitCode == 0,
            $"Settings-window probe failed.{Environment.NewLine}" +
            $"{await output}{Environment.NewLine}{await error}");
    }

    private static string FindProbe()
    {
        var candidate = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "SettingsWindowProbe",
            "release",
            "SettingsWindowProbe.exe"));
        Assert.True(
            File.Exists(candidate),
            $"Settings-window probe was not built: {candidate}");
        return candidate;
    }
}
