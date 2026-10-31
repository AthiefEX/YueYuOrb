using System.Globalization;
using System.Runtime.InteropServices;
using FloatDesk.Core.Danmaku;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace FloatDesk.App.Danmaku;

public interface IDanmakuNotificationSource
{
    bool HasAccess { get; }
    string Status { get; }
    Task<bool> RequestAccessAsync();
    Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken cancellationToken);
    void Reset();
}

public sealed class WindowsNotificationSource : IDanmakuNotificationSource
{
    public bool HasAccess { get; private set; }
    private readonly NotificationCursor _cursor = new();
    private int _generation;
    public string Status { get; private set; } = "尚未连接 Windows 通知";
    public static bool HasPackageIdentity
    {
        get { var length = 0; return GetCurrentPackageFullName(ref length, 0) != 15700; }
    }
    public async Task<bool> RequestAccessAsync()
    {
        if (!HasPackageIdentity)
        {
            HasAccess = false;
            Status = "便携模式无法申请通知权限，请使用通知增强包；预览仍可使用。";
            return false;
        }
        try
        {
            var granted = await UserNotificationListener.Current.RequestAccessAsync();
            HasAccess = granted == UserNotificationListenerAccessStatus.Allowed;
            Status = granted == UserNotificationListenerAccessStatus.Allowed ? "已授权，等待新通知" : "未获通知权限，请在 Windows 设置中允许通知访问。";
            Reset();
            return granted == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { HasAccess = false; Status = "无法申请通知权限，请检查通知增强包安装及系统权限。"; return false; }
    }
    public async Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken cancellationToken)
    {
        var generation = _generation;
        if (!HasPackageIdentity) { HasAccess = false; Status = "便携模式：需通知增强包才能接收；可先预览弹幕。"; return []; }
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            { HasAccess = false; _cursor.Reset(); Status = "需要通知授权，请在设置中点击“授权通知访问”。"; return []; }
            HasAccess = true;
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation) return [];
            var items = notifications.OrderBy(n => n.CreationTime).TakeLast(4096).ToArray();
            var newIds = _cursor.Accept(items.Select(Identity)).ToHashSet(StringComparer.Ordinal);
            List<DanmakuMessage> messages = [];
            foreach (var item in items.Where(n => newIds.Contains(Identity(n))))
            {
                try
                {
                    var platform = NotificationCursor.Platform(item.AppInfo.DisplayInfo.DisplayName);
                    if (platform is null) continue;
                    var text = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                    if (text is null || text.Count == 0) continue;
                    var title = Limit(text[0].Text, 256);
                    var body = Limit(string.Join(" ", text.Skip(1).Select(t => t.Text)), 2048);
                    if (string.IsNullOrWhiteSpace(body)) body = title;
                    messages.Add(new(Identity(item), platform, title, body));
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { /* Discard malformed notification, never log contents. */ }
            }
            Status = "已连接，仅显示启用后 Windows 实际提供的新通知";
            return messages;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { HasAccess = false; _cursor.Reset(); Status = "通知连接不可用，请重新检查授权。"; return []; }
    }
    public void Reset() { _generation++; _cursor.Reset(); }
    private static string Limit(string? text, int count) => (text ?? "")[..Math.Min(text?.Length ?? 0, count)];
    private static string Identity(UserNotification item) => item.AppInfo.AppUserModelId + ":" + item.Id.ToString(CultureInfo.InvariantCulture) + ":" + item.CreationTime.UtcTicks.ToString(CultureInfo.InvariantCulture);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, nint name);
}
