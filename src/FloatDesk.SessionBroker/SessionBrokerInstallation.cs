using System.Diagnostics;
using System.Security.Principal;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.SessionBroker;

internal static class SessionBrokerInstallation
{
    private const string ServiceName = "YueYuOrbSessionBroker";
    private static readonly string ProgramRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "YueYuOrb",
        "desktop-sessions");
    private static readonly string BrokerDirectory =
        Path.Combine(ProgramRoot, "broker");
    private static readonly string AgentDirectory =
        Path.Combine(ProgramRoot, "agent");
    private static readonly string InstalledBrokerPath =
        Path.Combine(BrokerDirectory, "YueYuOrb.SessionBroker.exe");

    public static int Install()
    {
        EnsureAdministrator();
        var sourceBroker = AppContext.BaseDirectory;
        var sourceAgent = Path.GetFullPath(Path.Combine(
            sourceBroker,
            "..",
            "agent"));
        if (!File.Exists(Path.Combine(
                sourceAgent,
                "YueYuOrb.SessionAgent.exe")) ||
            !File.Exists(Path.Combine(
                sourceAgent,
                "YueYuOrb.SessionAgent.exe.sha256")))
        {
            throw new InvalidOperationException(
                "The packaged session agent or manifest is missing.");
        }

        if (PathsEqual(sourceBroker, BrokerDirectory))
        {
            throw new InvalidOperationException(
                "Run the packaged broker executable to repair the service.");
        }

        if (TryRunSc("query", ServiceName))
        {
            _ = TryRunSc("stop", ServiceName);
        }
        CopyDirectoryWithRetry(sourceBroker, BrokerDirectory);
        CopyDirectoryWithRetry(sourceAgent, AgentDirectory);
        OrbStartupRegistration.MarkInstalledAndUpgradeOwnEntry();

        var binPath = Quote(InstalledBrokerPath) + " --service";
        if (TryRunSc("query", ServiceName))
        {
            RunSc(
                "config",
                ServiceName,
                "binPath=",
                binPath,
                "start=",
                "demand",
                "obj=",
                "LocalSystem",
                "DisplayName=",
                "月语桌面会话 Broker");
        }
        else
        {
            RunSc(
                "create",
                ServiceName,
                "binPath=",
                binPath,
                "start=",
                "demand",
                "obj=",
                "LocalSystem",
                "DisplayName=",
                "月语桌面会话 Broker");
        }

        RunSc(
            "description",
            ServiceName,
            "仅按固定清单启动月语会话助手，不接收任意可执行路径。");
        RunSc("start", ServiceName);
        return 0;
    }

    public static int Uninstall()
    {
        EnsureAdministrator();
        if (File.Exists(Path.Combine(ProgramRoot, "startup-guards", "Clash-Verge-Admin.active.json")))
            throw new InvalidOperationException("请先恢复Clash启动任务，再卸载服务，避免删除启动守卫导致Clash无法自启。恢复备份位于startup-guards目录。");
        OrbStartupRegistration.RestoreOwnEntryBeforeUninstall();
        if (TryRunSc("query", ServiceName))
        {
            _ = TryRunSc("stop", ServiceName);
        }
        DeleteServiceWithRetry();
        DeleteInstalledDirectory(BrokerDirectory);
        DeleteInstalledDirectory(AgentDirectory);
        TryDeleteEmpty(ProgramRoot);
        return 0;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (PathsEqual(source, destination))
            return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            File.Copy(
                file,
                Path.Combine(destination, Path.GetFileName(file)),
                overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void CopyDirectoryWithRetry(
        string source,
        string destination)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try
            {
                CopyDirectory(source, destination);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException)
                when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
            }
        }
    }

    private static void DeleteInstalledDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(ProgramRoot) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The broker uninstall path escaped the product directory.");
        }

        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }

    private static void TryDeleteEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) &&
                !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException(
                "Installing the desktop-session broker requires administrator approval.");
        }
    }

    private static void RunSc(params string[] arguments)
    {
        if (!TryRunSc(arguments))
        {
            throw new InvalidOperationException(
                "Service Control Manager rejected the broker service operation.");
        }
    }

    private static bool TryRunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null)
            return false;
        process.WaitForExit(15_000);
        return process.HasExited && process.ExitCode == 0;
    }

    private static void DeleteServiceWithRetry()
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (TryRunSc("query", ServiceName))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "The desktop-session broker service could not be deleted.");
            }

            _ = TryRunSc("delete", ServiceName);
            Thread.Sleep(250);
        }
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first).TrimEnd(
                Path.DirectorySeparatorChar),
            Path.GetFullPath(second).TrimEnd(
                Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
