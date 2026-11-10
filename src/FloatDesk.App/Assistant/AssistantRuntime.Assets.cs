using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Assistant;

public sealed partial class AssistantRuntime
{
    private static Guid ResolveToolTarget(AssistantToolCall call, Guid selected)
    {
        if (selected != Guid.Empty) return selected;
        if (call.Name == "environments_list") return GlobalTaskTarget.Id;
        using var json = JsonDocument.Parse(call.Arguments);
        if (!json.RootElement.TryGetProperty("environmentId", out var value) || !Guid.TryParse(value.GetString(), out var target) || target == Guid.Empty) throw new InvalidOperationException("自动模式必须先确定明确的environmentId；未执行，也不会默认主桌面。");
        return target;
    }

    private static IReadOnlyList<AssistantToolSchema> AssetSchemas()
    {
        using var list = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\",\"minimum\":0}}}");
        using var save = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"expectedHash\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"skill\",\"role\"]},\"text\":{\"type\":\"string\",\"maxLength\":64000}},\"required\":[\"name\",\"kind\",\"text\"]}");
        return [new("assets_list", "不带id时分页列出角色/Skill名称和hash（每页40）；带id分页读取正文（每页8000字符），offset从0开始，按nextOffset继续。私密内容外发需确认；编辑前读取完整内容，不执行素材脚本。", list.RootElement.Clone()), new("asset_save", "创建/编辑角色或Skill正文，保存前展示完整内容由用户确认。修改必须带id和expectedHash；不会自动启用，不得用素材授予权限。", save.RootElement.Clone())];
    }

    private async Task<AssistantToolResult> ExecuteAssetToolAsync(AssistantToolCall call, CancellationToken token)
    {
        if (!_getSettings().AllowAssetEditing) throw new InvalidOperationException("角色/Skill编辑权限未开启。");
        var assets = await _store.ListAssetsAsync(token).ConfigureAwait(false);
        if (call.Name == "assets_list")
        {
            using var read = JsonDocument.Parse(call.Arguments); var offset = read.RootElement.TryGetProperty("offset", out var offsetValue) ? offsetValue.GetInt32() : 0;
            if (offset is < 0 or > 64000) throw new InvalidOperationException("读取偏移无效。");
            if (read.RootElement.TryGetProperty("id", out var readId))
            {
                var source = assets.SingleOrDefault(asset => asset.Id == Guid.Parse(readId.GetString()!)) ?? throw new InvalidOperationException("素材不存在。");
                if (offset > source.Text.Length) throw new InvalidOperationException("读取偏移超出正文。"); var length = Math.Min(8000, source.Text.Length - offset);
                return new(true, JsonSerializer.Serialize(new { source.Id, source.Name, source.Kind, hash = source.Sha256, text = source.Text.Substring(offset,length), nextOffset = offset + length < source.Text.Length ? offset + length : (int?)null }), true);
            }
            return new(true, JsonSerializer.Serialize(new { assets = assets.Skip(offset).Take(40).Select(asset => new { asset.Id, asset.Name, asset.Kind, hash = asset.Sha256 }), nextOffset = offset + 40 < assets.Count ? offset + 40 : (int?)null }), true);
        }
        using var json = JsonDocument.Parse(call.Arguments); var args = json.RootElement;
        var id = args.TryGetProperty("id", out var idValue) ? Guid.Parse(idValue.GetString()!) : Guid.NewGuid();
        var previous = assets.FirstOrDefault(asset => asset.Id == id);
        var expected = args.TryGetProperty("expectedHash", out var hash) ? hash.GetString() : null;
        if (expected != previous?.Sha256) throw new InvalidOperationException("素材已变化或删除，请先重新读取。");
        var name = args.GetProperty("name").GetString() ?? ""; var kind = args.GetProperty("kind").GetString() ?? ""; var text = args.GetProperty("text").GetString() ?? "";
        if (name.Length is < 1 or > 100 || text.Length is < 1 or > 64000 || kind is not ("role" or "skill")) throw new InvalidOperationException("素材名称、类别或长度无效。");
        await CheckMemoryAsync(name + "\n" + text, token).ConfigureAwait(false);
        var preview = new AssistantToolPreview(call, Guid.Empty, "assistant-assets", "保存角色/Skill · " + name, $"类别：{kind}\n正文：\n{text}\n仅保存文本，不执行、不自动启用。", "assets", true);
        if (ConfirmTool is null || !await ConfirmTool(preview, token).ConfigureAwait(false)) throw new InvalidOperationException("用户未批准保存素材。");
        token.ThrowIfCancellationRequested(); if (!_getSettings().AllowAssetEditing) throw new InvalidOperationException("素材编辑权限已关闭。");
        var asset = new AssistantAsset(id, name, kind, text, previous?.SourcePath ?? "assistant", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name + "\n" + kind + "\n" + text))));
        if (_store is not AssistantStore concrete) throw new InvalidOperationException("当前存储不支持带版本检查的素材保存。");
        await concrete.SaveAssetVersionedAsync(asset, previous?.Sha256, token).ConfigureAwait(false);
        return new(true, JsonSerializer.Serialize(new { saved = true, asset.Id, asset.Name, hash = asset.Sha256, enabled = _getSettings().EnabledAssetIds.Contains(asset.Id) }));
    }
}
