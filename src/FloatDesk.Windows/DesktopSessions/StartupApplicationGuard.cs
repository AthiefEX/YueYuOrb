using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record StartupProcessIdentity(uint SessionId, string ExecutablePath);

public static class StartupApplicationGuard
{
    public static bool ShouldLaunch(uint currentSession, uint consoleSession, string executable,
        IReadOnlyList<StartupProcessIdentity> processes) =>
        consoleSession != uint.MaxValue && currentSession == consoleSession &&
        !processes.Any(process => process.SessionId == consoleSession &&
            string.Equals(Path.GetFullPath(process.ExecutablePath), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));

    public static int Run(string executable, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            return 2;
        var console = WTSGetActiveConsoleSessionId();
        var current = checked((uint)Process.GetCurrentProcess().SessionId);
        if (console == uint.MaxValue || current != console)
            return 0;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable).ToUpperInvariant())));
        using var gate = new Mutex(false, @"Local\YueYuOrb.StartupGuard." + key);
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                return 3;
            var identities = new List<StartupProcessIdentity>();
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                using (process)
                {
                    if (process.HasExited || process.SessionId != console)
                        continue;
                    var path = process.MainModule?.FileName;
                    if (path is null)
                        return 3;
                    identities.Add(new(console, path));
                }
            }
            if (!ShouldLaunch(current, console, executable, identities))
                return 0;
            var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var launched = Process.Start(start);
            if (launched is null)
                return 3;
            try { launched.WaitForInputIdle(5000); }
            catch (InvalidOperationException) { }
            return 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return 3;
        }
        finally
        {
            if (acquired)
                gate.ReleaseMutex();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
