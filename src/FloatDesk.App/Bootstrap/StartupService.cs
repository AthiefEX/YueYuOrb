using Microsoft.Win32;

namespace FloatDesk.App;

public sealed class StartupService
{
    public static async Task SetEnabledAsync(bool enabled)
    {
        if (!FloatDesk.App.Danmaku.WindowsNotificationSource.HasPackageIdentity) { SetEnabled(enabled); return; }
        var task = await global::Windows.ApplicationModel.StartupTask.GetAsync("YueYuOrbStartup");
        if (!enabled) { task.Disable(); return; }
        var state = await task.RequestEnableAsync();
        if (state is not (global::Windows.ApplicationModel.StartupTaskState.Enabled or global::Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
            throw new InvalidOperationException("开机启动未获允许，请在 Windows 设置 → 应用 → 启动中开启月语悬浮球。");
    }
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
