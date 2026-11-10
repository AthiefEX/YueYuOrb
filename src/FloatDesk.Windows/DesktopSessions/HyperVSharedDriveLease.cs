using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FloatDesk.Windows.DesktopSessions;

/// <summary>Owns only temporary folder aliases; existing drive roots are never removed.</summary>
public sealed class HyperVSharedDriveLease : IDisposable
{
    private readonly List<(string Name, string Target)> _aliases = [];
    private readonly List<string> _drives = [];
    private int _disposed;
    public IReadOnlyList<string> Drives => _drives;

    public static HyperVSharedDriveLease Create(IEnumerable<string> paths)
    {
        var lease = new HyperVSharedDriveLease();
        try
        {
            // Serialize letter allocation across all YueYu processes in this Windows session.
            using var mutex = new Mutex(false, @"Local\YueYuOrb.SharedDriveAllocation");
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (lease._drives.Count >= 16) throw new ArgumentException("最多共享 16 个位置。", nameof(paths));
                    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                        path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                        throw new ArgumentException("共享位置必须是本地磁盘或文件夹的完整路径。", nameof(paths));
                    var fullPath = Path.GetFullPath(path);
                    if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("共享位置不存在或无法访问。");
                    var root = Path.GetPathRoot(fullPath)!;
                    if (string.Equals(fullPath.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        lease._drives.Add(root.ToUpperInvariant());
                        continue;
                    }
                    var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse()
                        .Select(value => $"{(char)value}:")
                        .FirstOrDefault(name => QueryDosDevice(name, new char[32768], 32768) == 0 && Marshal.GetLastWin32Error() == 2)
                        ?? throw new InvalidOperationException("没有可用的临时盘符，请减少共享文件夹。");
                    var target = @"\??\" + fullPath.TrimEnd('\\');
                    if (!DefineDosDevice(0x1 | 0x8, letter, target)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    lease._aliases.Add((letter, target));
                    lease._drives.Add(letter + "\\");
                }
            }
            finally { mutex.ReleaseMutex(); }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var (name, target) in _aliases)
            // EXACT_MATCH prevents removing a mapping another process has replaced.
            _ = DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, name, target);
        _aliases.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string deviceName, string targetPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, [Out] char[] targetPath, int maxLength);
}
