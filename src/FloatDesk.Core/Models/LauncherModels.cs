namespace FloatDesk.Core.Models;

public enum LauncherKind { Exe, Shortcut, Folder }
public enum WheelActionKind
{
    RegionCapture,
    WindowCapture,
    MonitorCapture,
    AiBrowser,
    Notes,
    InputLock,
    Settings
}

public sealed record LauncherItem(
    Guid Id,
    string Name,
    LauncherKind Kind,
    string Path,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    int Order,
    bool ShowInWheel = false);
