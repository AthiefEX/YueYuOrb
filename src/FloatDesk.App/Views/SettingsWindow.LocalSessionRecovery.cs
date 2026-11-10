using FloatDesk.Windows.DesktopSessions;
using System.Diagnostics;
using System.Windows;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private bool _localSessionRecoveryRunning;

    private async Task RecoverLocalChildSessionAsync()
    {
        if (_localSessionRecoveryRunning)
            return;
        try
        {
            var native = new ChildSessionNative();
            var parent = checked((uint)Process.GetCurrentProcess().SessionId);
            if (!native.TryGetChildSessionId(out var child) || child is 0 or uint.MaxValue)
            {
                SetDesktopSessionStatus("当前没有可清理的本机子会话，可以重试启动。");
                return;
            }
            if (child == parent)
                throw new InvalidOperationException("拒绝清理主桌面会话。");
            if (MessageBox.Show(this,
                $"将注销当前桌面关联的子会话 {child}，关闭其中所有应用并丢失未保存内容。它可能属于其他程序。请确认不再使用该子会话后继续；主桌面不会注销。",
                "清理本机子会话", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _localSessionRecoveryRunning = true;
            SetDesktopSessionStatus("正在注销已确认的子会话，请等待完成。");
            var result = await Task.Run(() =>
            {
                if (!native.TryGetChildSessionId(out var current) || current != child || current == parent)
                    throw new InvalidOperationException("子会话关联已变化，已取消清理，请刷新后重试。");
                return native.TryLogoffSession(child);
            });
            SetDesktopSessionStatus(result
                ? "子会话已注销，请重新启动本机环境。"
                : "注销子会话失败，请确认权限和会话状态后重试。", result);
        }
        catch (Exception exception)
        {
            SetDesktopSessionStatus($"清理未完成：{exception.Message}", false);
        }
        finally
        {
            _localSessionRecoveryRunning = false;
        }
    }
}
