using FloatDesk.Core.Models;

namespace FloatDesk.Core.Launcher;

public sealed record LauncherImportResolution(
    string Name,
    LauncherKind Kind,
    string Path,
    string? WorkingDirectory);

public sealed record LauncherDropResult(
    IReadOnlyList<LauncherItem> Added,
    IReadOnlyList<string> Duplicates,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> Failed)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (Added.Count > 0)
                parts.Add($"已添加 {Added.Count} 个");
            if (Duplicates.Count > 0)
                parts.Add($"跳过 {Duplicates.Count} 个重复项");
            if (Unsupported.Count > 0)
                parts.Add($"{Unsupported.Count} 个文件不受支持");
            if (Failed.Count > 0)
                parts.Add($"{Failed.Count} 个文件读取失败");

            return parts.Count == 0
                ? "没有可添加的文件。"
                : $"{string.Join("，", parts)}。";
        }
    }
}

public interface ILauncherEntryResolver
{
    LauncherImportResolution? Resolve(string fullPath);
}
