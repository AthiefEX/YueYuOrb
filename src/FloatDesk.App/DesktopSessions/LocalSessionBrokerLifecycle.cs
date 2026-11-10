using FloatDesk.Core.Contracts;
using FloatDesk.Windows.DesktopSessions;
using System.Diagnostics;

namespace FloatDesk.App.DesktopSessions;

public static class LocalSessionBrokerLifecycle
{
    private const string ServiceName = "YueYuOrbSessionBroker";

    public static async Task SetRunningAsync(bool running)
    {
        var query = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        query.ArgumentList.Add("query");
        query.ArgumentList.Add(ServiceName);
        using var inspection = Process.Start(query) ?? throw new InvalidOperationException("无法检查分身服务。");
        var output = await inspection.StandardOutput.ReadToEndAsync();
        await inspection.WaitForExitAsync();
        if (inspection.ExitCode == 1060 && !running)
            return;
        if (inspection.ExitCode != 0)
            throw new InvalidOperationException("分身服务未安装或无法访问，请先在设置中安装/修复服务。");
        if (running && output.Contains("RUNNING", StringComparison.Ordinal))
            return;
        if (!running && output.Contains("STOPPED", StringComparison.Ordinal))
            return;
        var command = new ProcessStartInfo
        {
            FileName = query.FileName, UseShellExecute = true, Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        command.ArgumentList.Add(running ? "start" : "stop");
        command.ArgumentList.Add(ServiceName);
        using var process = Process.Start(command) ?? throw new InvalidOperationException("无法管理分身服务。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && process.ExitCode != (running ? 1056 : 1062) && process.ExitCode != 1061)
            throw new InvalidOperationException($"分身服务操作失败：{process.ExitCode}。");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var check = Process.Start(query) ?? throw new InvalidOperationException("无法确认服务状态。");
            var state = await check.StandardOutput.ReadToEndAsync();
            await check.WaitForExitAsync();
            if (check.ExitCode == 0 && state.Contains(running ? "RUNNING" : "STOPPED", StringComparison.Ordinal))
                return;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("服务状态切换超时，请检查服务后重试。");
    }
}

internal sealed class ServiceStartingSessionBroker : IChildSessionAgentBootstrapper
{
    public async Task<ChildSessionAgentBootstrap> LaunchAsync(
        Guid environmentId, Guid generation, uint windowsSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await LocalSessionBrokerLifecycle.SetRunningAsync(true);
        cancellationToken.ThrowIfCancellationRequested();
        return await new SessionBrokerClient().LaunchAsync(environmentId, generation, windowsSessionId, cancellationToken);
    }
}
