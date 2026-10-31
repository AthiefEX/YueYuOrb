namespace FloatDesk.Core.Danmaku;

/// <summary>Keeps notification identities only, never notification contents.</summary>
public sealed class NotificationCursor
{
    private HashSet<string>? _known;
    public IReadOnlyList<string> Accept(IEnumerable<string> identities)
    {
        var next = identities.Take(4096).ToHashSet(StringComparer.Ordinal);
        var added = _known is null ? [] : next.Where(id => !_known.Contains(id)).ToArray();
        _known = next;
        return added;
    }
    public void Reset() => _known = null;
    public static string? Platform(string name) => name.Trim().ToUpperInvariant() switch
    {
        "QQ" or "腾讯QQ" => "QQ", "TIM" or "腾讯TIM" => "TIM", "微信" or "WECHAT" or "WEIXIN" => "微信", _ => null
    };
}
