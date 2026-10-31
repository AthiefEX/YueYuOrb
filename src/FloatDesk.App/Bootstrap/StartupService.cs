using Microsoft.Win32;

namespace FloatDesk.App;

public sealed class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FloatDesk";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户启动项。");
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径。");
            key.SetValue(ValueName, $"\"{executable}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
