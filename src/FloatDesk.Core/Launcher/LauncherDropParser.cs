using FloatDesk.Core.Models;

namespace FloatDesk.Core.Launcher;

public static class LauncherDropParser
{
    private const int MaxNameLength = 80;

    public static LauncherDropResult Parse(
        IEnumerable<string> droppedPaths,
        IReadOnlyList<LauncherItem> existing,
        ILauncherEntryResolver resolver,
        Func<Guid>? createId = null)
    {
        ArgumentNullException.ThrowIfNull(droppedPaths);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(resolver);

        createId ??= Guid.NewGuid;
        var seenPaths = new HashSet<string>(
            existing
                .Select(item => NormalizeForComparison(item.Path))
                .Where(path => path is not null)!,
            StringComparer.OrdinalIgnoreCase);
        var added = new List<LauncherItem>();
        var duplicates = new List<string>();
        var unsupported = new List<string>();
        var failed = new List<string>();
        var nextOrder = existing.Count == 0
            ? 0
            : existing.Max(item => item.Order) + 1;

        foreach (var droppedPath in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(droppedPath))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(droppedPath);
            }
            catch (Exception exception)
                when (exception is ArgumentException or
                    NotSupportedException or
                    PathTooLongException)
            {
                unsupported.Add(droppedPath);
                continue;
            }

            if (!seenPaths.Add(fullPath))
            {
                duplicates.Add(fullPath);
                continue;
            }

            if (!File.Exists(fullPath) ||
                (!Path.GetExtension(fullPath).Equals(
                     ".exe",
                     StringComparison.OrdinalIgnoreCase) &&
                 !Path.GetExtension(fullPath).Equals(
                     ".lnk",
                     StringComparison.OrdinalIgnoreCase)))
            {
                unsupported.Add(fullPath);
                continue;
            }

            LauncherImportResolution? resolution;
            try
            {
                resolution = resolver.Resolve(fullPath);
            }
            catch
            {
                resolution = null;
            }

            if (resolution is null)
            {
                failed.Add(fullPath);
                continue;
            }

            var name = NormalizeName(resolution.Name, fullPath);
            added.Add(new LauncherItem(
                createId(),
                name,
                resolution.Kind,
                resolution.Path,
                [],
                resolution.WorkingDirectory,
                nextOrder++,
                ShowInWheel: false));
        }

        return new LauncherDropResult(
            added,
            duplicates,
            unsupported,
            failed);
    }

    private static string? NormalizeForComparison(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static string NormalizeName(string? name, string fullPath)
    {
        var candidate = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : name.Trim();
        if (candidate.Length > MaxNameLength)
            candidate = candidate[..MaxNameLength];
        return candidate;
    }
}
