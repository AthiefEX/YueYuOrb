using System.Globalization;
using System.Runtime.InteropServices;
using FloatDesk.Core.Danmaku;
using FloatDesk.Core.Models;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using Windows.Foundation;

namespace FloatDesk.App.Danmaku;

public interface IDanmakuNotificationSource
{
    bool HasAccess { get; }
    string Status { get; }
    Task<bool> RequestAccessAsync();
    Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken cancellationToken);
    void Reset();
    void ApplySettings(DanmakuSettings settings) { }
    IReadOnlyList<NotificationApplication> Applications => [];
}

public sealed class WindowsNotificationSource : IDanmakuNotificationSource
{
    public bool HasAccess { get; private set; }
    private readonly NotificationCursor _cursor = new();
    private int _generation;
    private readonly NotificationReadGate _reads = new();
    private UserNotificationListener? _listener;
    private TypedEventHandler<UserNotificationListener, UserNotificationChangedEventArgs>? _changed;
    private bool _subscriptionUnavailable;
    private DateTimeOffset _nextPermissionCheck;
    private DanmakuSettings _settings = new();
    public IReadOnlyList<NotificationApplication> Applications { get; private set; } = [];
    public void ApplySettings(DanmakuSettings settings) => _settings = settings.Normalize();
    public string Status { get; private set; } = "尚未连接 Windows 通知";
    public static bool HasPackageIdentity
    {
        get { var length = 0; return GetCurrentPackageFullName(ref length, 0) != 15700; }
    }
    public async Task<bool> RequestAccessAsync()
    {
        _subscriptionUnavailable = false;
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
            if (HasAccess) _subscriptionUnavailable = false;
            Status = granted == UserNotificationListenerAccessStatus.Allowed ? "已授权，等待新通知" : "未获通知权限，请在 Windows 设置中允许通知访问。";
            Reset();
            return granted == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        { HasAccess = false; Status = $"无法申请通知权限（0x{e.HResult:X8}），请检查通知增强包及系统权限。"; return false; }
    }
    public async Task<IReadOnlyList<DanmakuMessage>> PollAsync(CancellationToken cancellationToken)
    {
        var generation = _generation;
        if (!HasPackageIdentity) { HasAccess = false; Status = "便携模式：需通知增强包才能接收；可先预览弹幕。"; return []; }
        if (_subscriptionUnavailable) { HasAccess = false; return []; }
        if (_listener is not null && !_reads.HasPending && DateTimeOffset.UtcNow < _nextPermissionCheck) return [];
        try
        {
            var listener = _listener ?? UserNotificationListener.Current;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            { HasAccess = false; _cursor.Reset(); Status = "需要通知授权，请在设置中点击“授权通知访问”。"; return []; }
            HasAccess = true;
            _nextPermissionCheck = DateTimeOffset.UtcNow.AddSeconds(5);
            if (_listener is null)
            {
                var eventGeneration = _generation;
                _changed = (_, args) =>
                {
                    if (eventGeneration != Volatile.Read(ref _generation)) return;
                    if (args.ChangeKind == UserNotificationChangedKind.Added) _reads.Added(args.UserNotificationId);
                    else _reads.Removed(args.UserNotificationId);
                };
                try { listener.NotificationChanged += _changed; _listener = listener; }
                catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
                {
                    _changed = null; _subscriptionUnavailable = true; HasAccess = false;
                    Status = $"系统不支持通知事件监听（0x{e.HResult:X8}），自动读取已停止，避免反复触发客户端提醒。";
                    return [];
                }
            }
            if (!_reads.TryBeginRead()) return [];
            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation) return [];
            var items = notifications.OrderBy(n => n.CreationTime).TakeLast(4096).ToArray();
            _reads.Complete(items.Select(n => n.Id));
            List<(UserNotification Item, NotificationApplication App, string Identity)> entries = [];
            foreach (var item in items)
            {
                try
                {
                    var appId = item.AppInfo.AppUserModelId;
                    if (string.IsNullOrWhiteSpace(appId)) continue;
                    var name = Limit(item.AppInfo.DisplayInfo.DisplayName, 100);
                    var app = new NotificationApplication(appId, string.IsNullOrWhiteSpace(name) ? appId : name);
                    entries.Add((item, app, Identity(item, appId)));
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { }
            }
            Applications = entries.Select(e => e.App).Concat(Applications).DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Take(256).ToArray();
            var newIds = _cursor.Accept(entries.Select(e => e.Identity)).ToHashSet(StringComparer.Ordinal);
            var allowed = entries.Count(e => _settings.AllowsApplication(e.App.Id));
            List<DanmakuMessage> messages = [];
            foreach (var entry in entries.Where(e => newIds.Contains(e.Identity) && _settings.AllowsApplication(e.App.Id)))
            {
                try
                {
                    var item = entry.Item;
                    var text = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                    if (text is null || text.Count == 0) continue;
                    var title = Limit(text[0].Text, 256);
                    var body = Limit(string.Join(" ", text.Skip(1).Select(t => t.Text)), 2048);
                    if (string.IsNullOrWhiteSpace(body)) body = title;
                    messages.Add(new(entry.Identity, entry.App.Name, title, body, entry.App.Id));
                }
                catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException) { /* Discard malformed notification, never log contents. */ }
            }
            Status = $"通知权限已允许；观察到 {Applications.Count} 个应用、{items.Length} 条系统通知，所选来源 {allowed} 条，本次新消息 {messages.Count} 条。仅显示启用后的新通知。";
            return messages;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            HandleReadFailure(generation, e.HResult);
            return [];
        }
    }
    internal void HandleReadFailure(int generation, int errorCode)
    {
        if (generation != Volatile.Read(ref _generation)) return;
        HasAccess = false; _subscriptionUnavailable = true;
        Reset();
        Status = $"通知读取失败（0x{errorCode:X8}），自动重试已停止，请检查权限后重新授权。";
    }
    public void Reset()
    {
        Interlocked.Increment(ref _generation); _cursor.Reset(); _reads.Reset(); _nextPermissionCheck = default;
        if (_listener is not null && _changed is not null)
        {
            try { _listener.NotificationChanged -= _changed; }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        _listener = null; _changed = null;
    }
    private static string Limit(string? text, int count) => (text ?? "")[..Math.Min(text?.Length ?? 0, count)];
    private static string Identity(UserNotification item, string appId) => appId + ":" + item.Id.ToString(CultureInfo.InvariantCulture) + ":" + item.CreationTime.UtcTicks.ToString(CultureInfo.InvariantCulture);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, nint name);
}
