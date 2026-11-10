using Microsoft.Win32;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App;

public sealed class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FloatDesk";
    private const string StartupArgument = "--startup";

    internal static bool ShouldNotifyExistingInstance(IEnumerable<string> arguments) =>
        !arguments.Any(argument => string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase));

    internal static string BuildStartupCommand(string executable, string? guardExecutable = null) =>
        OrbStartupRegistration.BuildCommand(executable, guardExecutable);

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项。");
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径。");
            key.SetValue(ValueName, BuildStartupCommand(executable, OrbStartupRegistration.GetVerifiedGuard()));
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
