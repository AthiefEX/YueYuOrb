using System.Security.Cryptography;
using Microsoft.Win32;

namespace FloatDesk.Windows.DesktopSessions;

/// <summary>Guards only this application's own Run entry; never edits other startup applications.</summary>
public static class OrbStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string AgentDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YueYuOrb", "desktop-sessions", "agent");
    public static string GuardPath => Path.Combine(AgentDirectory, "YueYuOrb.SessionAgent.exe");
    private static string MarkerPath => Path.Combine(AgentDirectory, "startup-guard.v1");

    public static string BuildCommand(string executable, string? guard = null) => guard is null
        ? $"\"{executable}\" --startup"
        : $"\"{guard}\" --guard-startup-app \"{executable}\" -- --startup";

    public static string? GetVerifiedGuard()
    {
        try
        {
            var dll = Path.Combine(AgentDirectory, "YueYuOrb.SessionAgent.dll");
            if (!File.Exists(GuardPath) || !File.Exists(MarkerPath)) return null;
            using var stream = File.OpenRead(dll);
            return string.Equals(Convert.ToHexString(SHA256.HashData(stream)),
                File.ReadAllText(MarkerPath).Trim(), StringComparison.OrdinalIgnoreCase) ? GuardPath : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void MarkInstalledAndUpgradeOwnEntry()
    {
        using (var stream = File.OpenRead(Path.Combine(AgentDirectory, "YueYuOrb.SessionAgent.dll")))
            File.WriteAllText(MarkerPath, Convert.ToHexString(SHA256.HashData(stream)));
        RewriteOwnEntry(install: true);
    }

    public static void RestoreOwnEntryBeforeUninstall() => RewriteOwnEntry(install: false);

    internal static string? RewriteCommand(string command, string guard, bool install)
    {
        var prefix = install ? "\"" : $"\"{guard}\" --guard-startup-app \"";
        var suffix = install ? "\" --startup" : "\" -- --startup";
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !command.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            command.Length <= prefix.Length + suffix.Length) return null;
        var target = command[prefix.Length..^suffix.Length];
        if (target.Contains('"') || !Path.IsPathFullyQualified(target) ||
            !(string.Equals(Path.GetFileName(target), "YueYuOrb.exe", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(Path.GetFileName(target), "FloatDesk.exe", StringComparison.OrdinalIgnoreCase))) return null;
        return BuildCommand(target, install ? guard : null);
    }

    private static void RewriteOwnEntry(bool install)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue("FloatDesk") is not string command) return;
        var replacement = RewriteCommand(command, GuardPath, install);
        if (replacement is not null) key.SetValue("FloatDesk", replacement);
        else if (!install && command.Contains(GuardPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("月语自启入口已改变，请先恢复该入口再卸载会话守卫。");
    }
}
