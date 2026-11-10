using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public static partial class AssistantAssetLoader
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] RoleFields = ["description", "personality", "scenario", "first_mes", "mes_example", "system_prompt", "post_history_instructions", "systemPrompt", "content"];

    public static async Task<AssistantAsset> LoadAsync(string path, string kind, CancellationToken token)
    {
        if (kind is not ("skill" or "role")) throw new ArgumentException("请选择Skill或角色素材。", nameof(kind));
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full)) full = Path.Combine(full, "SKILL.md");
        RejectLinks(full);
        if (!File.Exists(full) || new FileInfo(full).Length > 4 * 1024 * 1024) throw new InvalidDataException("素材文件不存在或超过4MiB。");
        var bytes = await File.ReadAllBytesAsync(full, token).ConfigureAwait(false);
        string text, name = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        if (kind == "role" && (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)))
        {
            var json = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? ReadPngCard(bytes) : Utf8.GetString(bytes);
            (name, text) = ReadRole(json);
        }
        else
        {
            if (!extension.Equals(".md", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("支持Markdown、文本角色或JSON/PNG角色卡；Skill使用SKILL.md。");
            text = Utf8.GetString(bytes);
            if (kind == "skill")
            {
                if (!Path.GetFileName(full).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择Skill目录中的SKILL.md。");
                var match = SkillName().Match(text); name = match.Success ? match.Groups[1].Value.Trim().Trim('\'', '"') : new DirectoryInfo(Path.GetDirectoryName(full)!).Name;
                var root = Path.GetDirectoryName(full)!;
                var builder = new StringBuilder(text); var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var links = MarkdownLinks().Matches(text);
                foreach (Match link in links)
                {
                    var relative = link.Groups[1].Value.Split('#')[0].Trim().Trim('<', '>');
                    if (relative.Length == 0) continue;
                    if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
                    { if (absolute.Scheme is "http" or "https") continue; throw new InvalidDataException("Skill资源不能引用本机绝对路径或特殊协议。"); }
                    var resource = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(relative)));
                    if (!resource.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Skill资源超出导入目录。");
                    RejectLinks(resource);
                    var suffix = Path.GetExtension(resource);
                    if (!suffix.Equals(".md", StringComparison.OrdinalIgnoreCase) && !suffix.Equals(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!loaded.Add(resource)) continue;
                    if (loaded.Count > 8 || !File.Exists(resource) || new FileInfo(resource).Length > 64000) throw new InvalidDataException("Skill文本资源缺失或超过数量/大小限制。");
                    builder.Append("\n\n[参考资料：").Append(Path.GetFileName(resource)).Append("]\n").Append(await File.ReadAllTextAsync(resource, Utf8, token).ConfigureAwait(false));
                    if (builder.Length > 64000) throw new InvalidDataException("Skill总内容超过64000字符。");
                }
                text = builder.ToString();
            }
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl) || string.IsNullOrWhiteSpace(text) || text.Length > 64000) throw new InvalidDataException("素材名称为空或内容超过限制。");
        return new(Guid.NewGuid(), name, kind, text, full, Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text))));
    }

    private static (string Name, string Text) ReadRole(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 }); var root = document.RootElement;
            if (root.TryGetProperty("spec", out var spec))
            { if (spec.GetString() != "chara_card_v2") throw new InvalidDataException("仅支持Character Card V2或普通JSON角色。"); root = root.GetProperty("data"); }
            var name = root.GetProperty("name").GetString() ?? ""; var text = new StringBuilder();
            foreach (var field in RoleFields)
                if (root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } content)
                    text.Append(field).Append(": ").Append(content).Append('\n');
            return (name, text.ToString());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new InvalidDataException("角色卡JSON格式无效，要求name及角色文本。"); }
    }

    private static string ReadPngCard(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("PNG角色卡头部无效。");
        for (var offset = 8; offset <= bytes.Length - 12;)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) throw new InvalidDataException("PNG角色卡块长度无效。");
            var size = checked((int)length);
            if (bytes.AsSpan(offset + 4, 4).SequenceEqual("tEXt"u8))
            {
                var content = bytes.AsSpan(offset + 8, size); var separator = content.IndexOf((byte)0);
                if (separator == 5 && content[..5].SequenceEqual("chara"u8))
                {
                    if (size > 131072) throw new InvalidDataException("PNG角色卡文本过大。");
                    try { return Utf8.GetString(Convert.FromBase64String(Encoding.ASCII.GetString(content[(separator + 1)..]))); }
                    catch (FormatException) { throw new InvalidDataException("PNG角色卡编码无效。"); }
                }
            }
            offset += size + 12;
        }
        throw new InvalidDataException("PNG中未找到受支持的chara文本。请导出为Character Card V2 JSON后导入。");
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("素材不能来自文件或目录链接。");
    }

    [GeneratedRegex("(?m)^name:\\s*(.+)$", RegexOptions.CultureInvariant)] private static partial Regex SkillName();
    [GeneratedRegex("\\[[^\\]]*\\]\\(([^)]+)\\)", RegexOptions.CultureInvariant)] private static partial Regex MarkdownLinks();
}
