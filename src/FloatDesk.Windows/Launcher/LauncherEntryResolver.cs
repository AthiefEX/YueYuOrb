using System.Runtime.InteropServices;
using System.Text;
using FloatDesk.Core.Launcher;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Launcher;

public sealed class LauncherEntryResolver : ILauncherEntryResolver
{
    public LauncherImportResolution? Resolve(string fullPath)
    {
        if (!File.Exists(fullPath))
            return null;

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return ResolveExecutable(fullPath);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ResolveShortcut(fullPath);
        return null;
    }

    private static LauncherImportResolution ResolveExecutable(string path) =>
        new(
            Path.GetFileNameWithoutExtension(path),
            LauncherKind.Exe,
            path,
            Path.GetDirectoryName(path));

    private static LauncherImportResolution? ResolveShortcut(string path)
    {
        var details = ReadShortcut(path);
        if (details is null ||
            string.IsNullOrWhiteSpace(details.TargetPath) ||
            (!File.Exists(details.TargetPath) &&
             !Directory.Exists(details.TargetPath)))
            return null;

        return new LauncherImportResolution(
            Path.GetFileNameWithoutExtension(path),
            LauncherKind.Shortcut,
            path,
            null);
    }

    public static IReadOnlyList<LauncherIconSource> GetIconSources(
        string fullPath)
    {
        var sources = new List<LauncherIconSource>
        {
            new(NormalizePath(fullPath), 0)
        };
        if (!Path.GetExtension(fullPath).Equals(
                ".lnk",
                StringComparison.OrdinalIgnoreCase))
        {
            return sources;
        }

        var details = ReadShortcut(fullPath);
        if (details is null)
            return sources;
        if (!string.IsNullOrWhiteSpace(details.TargetPath))
        {
            sources.Add(new(
                NormalizePath(details.TargetPath),
                0));
        }
        if (!string.IsNullOrWhiteSpace(details.IconPath))
        {
            sources.Add(new(
                NormalizePath(
                    ResolveShortcutRelativePath(
                        fullPath,
                        details.IconPath)),
                details.IconIndex));
        }

        return sources
            .DistinctBy(
                source => $"{source.Path}\n{source.Index}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ShortcutDetails? ReadShortcut(string path)
    {
        object? shellLinkObject = null;
        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            ((IPersistFile)shellLinkObject).Load(path, 0);

            var target = new StringBuilder(32768);
            shellLink.GetPath(target, target.Capacity, nint.Zero, 0);
            var iconPath = new StringBuilder(32768);
            shellLink.GetIconLocation(
                iconPath,
                iconPath.Capacity,
                out var iconIndex);
            return new ShortcutDetails(
                target.ToString(),
                iconPath.ToString(),
                iconIndex);
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (shellLinkObject is not null &&
                Marshal.IsComObject(shellLinkObject))
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static string ResolveShortcutRelativePath(
        string shortcutPath,
        string iconPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(iconPath);
        if (Path.IsPathRooted(expanded))
            return expanded;

        var directory = Path.GetDirectoryName(shortcutPath);
        return string.IsNullOrWhiteSpace(directory)
            ? expanded
            : Path.Combine(directory, expanded);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return path;
        }
    }

    private sealed record ShortcutDetails(
        string TargetPath,
        string IconPath,
        int IconIndex);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maxPath,
            nint findData,
            uint flags);

        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name,
            int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory,
            int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments,
            int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
            int iconPathLength,
            out int iconIndex);
        void SetIconLocation(
            [MarshalAs(UnmanagedType.LPWStr)] string iconPath,
            int iconIndex);
        void SetRelativePath(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            uint reserved);
        void Resolve(nint windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load(
            [MarshalAs(UnmanagedType.LPWStr)] string fileName,
            uint mode);
        void Save(
            [MarshalAs(UnmanagedType.LPWStr)] string? fileName,
            [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
