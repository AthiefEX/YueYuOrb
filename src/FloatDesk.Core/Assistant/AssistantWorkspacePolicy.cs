namespace FloatDesk.Core.Assistant;

public static class AssistantWorkspacePolicy
{
    public static AssistantSettings SetSearch(AssistantSettings settings, Guid profile, string model, bool enabled)
    {
        if (enabled && (string.IsNullOrWhiteSpace(model) || model.Length > 200 || !settings.Profiles.Any(item => item.Id == profile && item.Enabled && item.ModelEnabled))) throw new InvalidOperationException("请先启用有效连接并选择模型。");
        if (!enabled && settings.SearchProfileId != profile) return settings;
        if (enabled && settings.SearchEnabled && settings.SearchProfileId is { } current)
        {
            var previousModel = settings.SearchModelId.Length > 0 ? settings.SearchModelId : settings.Profiles.FirstOrDefault(item => item.Id == current)?.Model;
            if (current != profile || previousModel != model) throw new InvalidOperationException("只能启用一个联网搜索模型。请先关闭当前搜索模型，再启用新的模型。");
        }
        return settings with { SearchEnabled = enabled, SearchProfileId = enabled ? profile : settings.SearchProfileId, SearchModelId = enabled ? model : settings.SearchModelId,
            Profiles = enabled ? settings.Profiles.Select(item => item.Id == profile && string.IsNullOrWhiteSpace(item.Model) ? item with { Model = model } : item).ToArray() : settings.Profiles };
    }

    public static IReadOnlyList<Guid> SetAsset(AssistantSettings settings, IReadOnlyList<AssistantAsset> assets, Guid id, bool enabled)
    {
        var asset = assets.SingleOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("素材已移除。");
        var roleIds = assets.Where(item => item.Kind == "role").Select(item => item.Id).ToHashSet();
        return enabled ? settings.EnabledAssetIds.Where(existing => asset.Kind != "role" || !roleIds.Contains(existing)).Append(id).Distinct().ToArray() : settings.EnabledAssetIds.Where(existing => existing != id).ToArray();
    }

    public static IReadOnlyList<AssistantAsset> ActiveAssets(AssistantSettings settings, IReadOnlyList<AssistantAsset> assets, IReadOnlyList<Guid>? mentioned = null)
    {
        var lastRole = settings.EnabledAssetIds.LastOrDefault(id => assets.Any(asset => asset.Id == id && asset.Kind == "role"));
        return assets.Where(asset => asset.Kind == "role" ? asset.Id == lastRole : settings.EnabledAssetIds.Contains(asset.Id) || mentioned?.Contains(asset.Id) == true).ToArray();
    }
}
