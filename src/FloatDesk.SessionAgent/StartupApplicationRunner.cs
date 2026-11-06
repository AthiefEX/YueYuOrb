using FloatDesk.Core.Models;
using System.Diagnostics;

namespace FloatDesk.SessionAgent;

public static class StartupApplicationRunner
{
    public static async Task<CommandResult> RunAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != SessionCommandKind.LaunchApplication ||
            !SessionStartupCommandCodec.TryDecode(
                command.Parameters,
                out var entry) ||
            entry is null ||
            !entry.Enabled)
        {
            return Failed(command, "InvalidStartupCommand");
        }

        if (!Path.IsPathFullyQualified(entry.ExecutablePath) ||
            (!string.IsNullOrWhiteSpace(entry.WorkingDirectory) &&
             !Path.IsPathFullyQualified(entry.WorkingDirectory)))
        {
            return Failed(command, "StartupPathInvalid");
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(entry.ExecutablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
            PathTooLongException)
        {
            return Failed(command, "StartupPathInvalid");
        }

        if (!File.Exists(executablePath))
            return Failed(command, "StartupPathMissing");

        var extension = Path.GetExtension(executablePath);
        if (string.Equals(
                extension,
                ".lnk",
                StringComparison.OrdinalIgnoreCase))
        {
            if (entry.Arguments.Count > 0 ||
                !string.IsNullOrWhiteSpace(entry.WorkingDirectory))
            {
                return Failed(
                    command,
                    "ShortcutOverridesNotSupported");
            }
        }
        else if (!string.Equals(
                     extension,
                     ".exe",
                     StringComparison.OrdinalIgnoreCase))
        {
            return Failed(command, "StartupExecutableTypeUnsupported");
        }

        if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory) &&
            !Directory.Exists(entry.WorkingDirectory))
        {
            return Failed(command, "StartupWorkingDirectoryMissing");
        }

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = string.Equals(
                    extension,
                    ".lnk",
                    StringComparison.OrdinalIgnoreCase),
                CreateNoWindow = false
            };
            if (!string.IsNullOrWhiteSpace(entry.WorkingDirectory))
                startInfo.WorkingDirectory = entry.WorkingDirectory;
            if (!startInfo.UseShellExecute)
            {
                foreach (var argument in entry.Arguments)
                    startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
        }
        catch
        {
            return Failed(command, "StartupLaunchFailed");
        }

        if (process is null)
            return Failed(command, "StartupLaunchFailed");
        using var ownedProcess = process;
        if (!entry.WaitForExit)
            return Succeeded(command);

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
            return process.ExitCode == 0
                ? Succeeded(command)
                : Failed(command, "StartupProcessExitedWithError");
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new CommandResult(
                command.CommandId,
                false,
                true,
                "OutcomeUnknown");
        }
    }

    private static CommandResult Succeeded(SessionCommand command) =>
        new(command.CommandId, true, false, null);

    private static CommandResult Failed(
        SessionCommand command,
        string errorCode) =>
        new(command.CommandId, false, false, errorCode);
}
