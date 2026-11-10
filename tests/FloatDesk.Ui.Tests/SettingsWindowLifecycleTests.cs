using System.Diagnostics;

namespace FloatDesk.Ui.Tests;

public sealed class SettingsWindowLifecycleTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--console-power-ui")]
    [InlineData("--console-close-policy-ui")]
    [InlineData("--glass-frame-ui")]
    public async Task ProbeOpensAndReopensDefaultAndDesktopSessionSettings(string probeArgument)
    {
        var probe = FindProbe();
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = probe,
            Arguments = probeArgument,
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
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "SettingsWindowProbe.exe"),
            Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "SettingsWindowProbe",
            "release",
            "SettingsWindowProbe.exe")),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "tools",
                "SettingsWindowProbe",
                "bin",
                "Release",
                "net10.0-windows10.0.19041.0",
                "SettingsWindowProbe.exe"))
        };
        var candidate = candidates.FirstOrDefault(path => File.Exists(path) &&
            File.Exists(Path.ChangeExtension(path, ".dll")) &&
            File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")));
        Assert.True(candidate is not null,
            $"Settings-window probe was not built: {string.Join("; ", candidates)}");
        return candidate!;
    }
}
