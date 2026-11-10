using System.Globalization;
using System.Text;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Network;

public static class SteamHostsPatch
{
    private const string Prefix = "\r\n# BEGIN YueYuOrb Steam ";
    public static byte[] Add(byte[] original, SteamNetworkSettings settings, Guid owner, int processId, long creationTicks)
    {
        if (original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            throw new IOException("hosts 使用了不支持的 UTF-16 编码，未作修改。");
        var text = Encoding.ASCII.GetString(original);
        if (text.Contains("# BEGIN YueYuOrb Steam ", StringComparison.Ordinal)) throw new IOException("仍有月语 hosts 租约，请先恢复网络配置。");
        var domains = SteamNetworkPolicy.Domains(settings);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Skip(1).Any(field => domains.Contains(field, StringComparer.OrdinalIgnoreCase)))
                throw new IOException("hosts 已有 Steam 自定义映射；为保留原配置，请先处理冲突。");
        }
        var block = Prefix + owner.ToString("N") + "\r\n# OWNER " + processId.ToString(CultureInfo.InvariantCulture) + " " + creationTicks.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            string.Join("\r\n", domains.Select(domain => "127.0.0.1 " + domain)) + "\r\n# END YueYuOrb Steam " + owner.ToString("N") + "\r\n";
        return original.Concat(Encoding.ASCII.GetBytes(block)).ToArray();
    }

    public static byte[] Remove(byte[] current, Guid owner)
    {
        var start = Encoding.ASCII.GetBytes(Prefix + owner.ToString("N") + "\r\n");
        var end = Encoding.ASCII.GetBytes("# END YueYuOrb Steam " + owner.ToString("N") + "\r\n");
        var offset = current.AsSpan().IndexOf(start);
        if (offset < 0) return current;
        var endOffset = current.AsSpan(offset + start.Length).IndexOf(end);
        if (endOffset < 0) throw new IOException("hosts 恢复标记不完整，已保留文件以避免误删。");
        var after = offset + start.Length + endOffset + end.Length;
        return current.AsSpan(0, offset).ToArray().Concat(current.AsSpan(after).ToArray()).ToArray();
    }

    internal static IReadOnlyList<(Guid Owner, int ProcessId, long Ticks)> Owners(byte[] current)
    {
        var text = Encoding.ASCII.GetString(current);
        var result = new List<(Guid, int, long)>();
        var offset = 0;
        while ((offset = text.IndexOf(Prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var lines = text[(offset + Prefix.Length)..].Split("\r\n", 3, StringSplitOptions.None);
            if (lines.Length < 2 || !Guid.TryParseExact(lines[0], "N", out var id)) throw new IOException("hosts 租约标记无效。");
            var fields = lines[1].Split(' ');
            if (fields.Length != 4 || fields[0] != "#" || fields[1] != "OWNER" ||
                !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) throw new IOException("hosts 租约进程标记无效。");
            result.Add((id, pid, ticks)); offset += Prefix.Length;
        }
        return result;
    }
}
