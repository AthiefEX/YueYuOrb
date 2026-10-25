namespace FloatDesk.Core.Models;

public enum LauncherKind { Exe, Shortcut, Folder }

public sealed record LauncherItem(
    Guid Id,
    string Name,
    LauncherKind Kind,
    string Path,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int Order);
