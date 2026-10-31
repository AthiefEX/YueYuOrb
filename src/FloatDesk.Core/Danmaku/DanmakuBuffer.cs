using FloatDesk.Core.Models;

namespace FloatDesk.Core.Danmaku;

public sealed record DanmakuMessage(string Id, string Platform, string Title, string Text);

public sealed class DanmakuBuffer
{
    private readonly Queue<(DanmakuMessage Message, DateTimeOffset Time)> _pending = new();
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();
    public int Count => _pending.Count;
    public bool Enqueue(DanmakuMessage message, DateTimeOffset now)
    {
        if (!_seen.Add(message.Id)) return false;
        _seenOrder.Enqueue(message.Id);
        while (_seenOrder.Count > 1024) _seen.Remove(_seenOrder.Dequeue());
        while (_pending.Count >= 100) _pending.Dequeue();
        _pending.Enqueue((message, now));
        return true;
    }
    public DanmakuMessage? Take(DateTimeOffset now)
    {
        while (_pending.TryDequeue(out var item))
            if (now - item.Time <= TimeSpan.FromSeconds(30)) return item.Message;
        return null;
    }
    public void Clear() { _pending.Clear(); _seen.Clear(); _seenOrder.Clear(); }
    public static bool Matches(DanmakuMessage message, DanmakuSettings settings)
    {
        var enabled = message.Platform switch { "QQ" => settings.QQ, "TIM" => settings.TIM, "微信" => settings.WeChat, _ => false };
        var names = Lines(settings.TitleFilter);
        return enabled && !string.IsNullOrWhiteSpace(message.Text) &&
            (names.Length == 0 || names.Any(n => message.Title.Contains(n, StringComparison.OrdinalIgnoreCase)));
    }
    public static bool IsHighlighted(DanmakuMessage message, DanmakuSettings settings) => Lines(settings.HighlightKeywords)
        .Any(k => message.Title.Contains(k, StringComparison.OrdinalIgnoreCase) || message.Text.Contains(k, StringComparison.OrdinalIgnoreCase));
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
