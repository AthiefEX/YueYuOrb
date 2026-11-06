using System.Diagnostics;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record HyperVFeatureOperationResult(bool Succeeded, bool RestartRequired, int ExitCode);
public sealed record HyperVFeatureStatus(
    bool FeatureEnabled,
    bool HypervisorLaunchEnabled,
    bool HypervisorRunning,
    bool ManagementToolsAvailable,
    bool RestartRequired,
    string? Detail);

public static class HyperVFeatureManagement
{
    public static ProcessStartInfo CreateCommand(bool enable)
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dism.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        command.ArgumentList.Add("/Online");
        command.ArgumentList.Add(enable ? "/Enable-Feature" : "/Disable-Feature");
        command.ArgumentList.Add("/FeatureName:Microsoft-Hyper-V-All");
        if (enable)
            command.ArgumentList.Add("/All");
        command.ArgumentList.Add("/NoRestart");
        return command;
    }

    public static ProcessStartInfo CreateManagementToolsCommand()
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "dism.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        command.ArgumentList.Add("/Online");
        command.ArgumentList.Add("/Enable-Feature");
        command.ArgumentList.Add("/FeatureName:Microsoft-Hyper-V-Management-PowerShell");
        command.ArgumentList.Add("/All");
        command.ArgumentList.Add("/NoRestart");
        return command;
    }

    public static ProcessStartInfo CreateBootConfigurationCommand(bool enable)
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "bcdedit.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        command.ArgumentList.Add("/set");
        command.ArgumentList.Add("{current}");
        command.ArgumentList.Add("hypervisorlaunchtype");
        command.ArgumentList.Add(enable ? "auto" : "off");
        return command;
    }

    public static ProcessStartInfo CreateFeatureQueryCommand()
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "dism.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        command.ArgumentList.Add("/Online");
        command.ArgumentList.Add("/English");
        command.ArgumentList.Add("/Get-FeatureInfo");
        command.ArgumentList.Add("/FeatureName:Microsoft-Hyper-V-All");
        return command;
    }

    public static ProcessStartInfo CreateBootQueryCommand()
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "bcdedit.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        command.ArgumentList.Add("/enum");
        command.ArgumentList.Add("{current}");
        return command;
    }

    public static ProcessStartInfo CreateSystemInfoCommand()
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "systeminfo.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        command.ArgumentList.Add("/FO");
        command.ArgumentList.Add("LIST");
        return command;
    }

    public static ProcessStartInfo CreateManagementToolsQueryCommand()
    {
        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        command.ArgumentList.Add("-NoLogo");
        command.ArgumentList.Add("-NoProfile");
        command.ArgumentList.Add("-NonInteractive");
        command.ArgumentList.Add("-ExecutionPolicy");
        command.ArgumentList.Add("Bypass");
        command.ArgumentList.Add("-Command");
        command.ArgumentList.Add(
            "[pscustomobject]@{ available = $null -ne (Get-Command Get-VM -ErrorAction SilentlyContinue) } | ConvertTo-Json -Compress");
        return command;
    }

    public static bool ParseFeatureEnabled(string output) =>
        output.Contains("State : Enabled", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("状态 : 已启用", StringComparison.OrdinalIgnoreCase);

    public static bool ParseHypervisorLaunchEnabled(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Contains("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("auto", StringComparison.OrdinalIgnoreCase));

    public static bool ParseHypervisorPresent(string output) =>
        output.Contains("A hypervisor has been detected", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("已检测到虚拟机监控程序", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("检测到虚拟机监控程序", StringComparison.OrdinalIgnoreCase);

    public static bool ParseManagementToolsAvailable(string output) =>
        output.Contains("\"available\":true", StringComparison.OrdinalIgnoreCase);

    public static async Task<HyperVFeatureStatus> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var feature = await RunQueryAsync(
            CreateFeatureQueryCommand(),
            cancellationToken).ConfigureAwait(false);
        var boot = await RunQueryAsync(
            CreateBootQueryCommand(),
            cancellationToken).ConfigureAwait(false);
        var system = await RunQueryAsync(
            CreateSystemInfoCommand(),
            cancellationToken).ConfigureAwait(false);
        var managementTools = await RunQueryAsync(
            CreateManagementToolsQueryCommand(),
            cancellationToken).ConfigureAwait(false);
        var featureEnabled = feature.ExitCode == 0 && ParseFeatureEnabled(feature.Output);
        var bootEnabled = boot.ExitCode == 0 && ParseHypervisorLaunchEnabled(boot.Output);
        var restartRequired = feature.Output.Contains("Restart Needed : Yes", StringComparison.OrdinalIgnoreCase) ||
            feature.Output.Contains("需要重启", StringComparison.OrdinalIgnoreCase);
        var detail = feature.ExitCode != 0
            ? feature.Error
            : boot.ExitCode != 0
                ? boot.Error
                : null;
        return new(
            featureEnabled,
            bootEnabled,
            system.ExitCode == 0 && ParseHypervisorPresent(system.Output),
            managementTools.ExitCode == 0 &&
                ParseManagementToolsAvailable(managementTools.Output),
            restartRequired,
            detail);
    }

    public static HyperVFeatureOperationResult ClassifyExitCode(int exitCode) =>
        new(exitCode is 0 or 3010, exitCode == 3010, exitCode);

    private static async Task<QueryResult> RunQueryAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法读取 Hyper-V 系统状态。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }

    private sealed record QueryResult(int ExitCode, string Output, string Error);

}
