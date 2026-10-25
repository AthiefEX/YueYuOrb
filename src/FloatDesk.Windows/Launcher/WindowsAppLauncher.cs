using System.Diagnostics;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Launcher;

public sealed record LauncherProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    bool UseShellExecute);

public sealed class WindowsAppLauncher : IAppLauncher
{
    public async Task<OperationResult> LaunchAsync(
        LauncherItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
            return OperationResult.Fail("TargetMissing", "目标不存在，请重新选择。");

        try
        {
            var spec = BuildSpec(item);
            var startInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                UseShellExecute = spec.UseShellExecute,
                WorkingDirectory = spec.WorkingDirectory ?? string.Empty
            };
            foreach (var argument in spec.Arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            await Task.CompletedTask;
            return process is null
                ? OperationResult.Fail("LaunchFailed", "系统未启动该应用。")
                : OperationResult.Ok();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return OperationResult.Fail("LaunchFailed", exception.Message);
        }
    }

    public static LauncherProcessSpec BuildSpec(LauncherItem item)
    {
        var shell = item.Kind is LauncherKind.Shortcut or LauncherKind.Folder;
        return new LauncherProcessSpec(
            item.Path,
            shell ? [] : item.Arguments.ToArray(),
            shell ? null : item.WorkingDirectory,
            shell);
    }
}
