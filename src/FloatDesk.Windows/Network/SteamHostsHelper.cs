using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using FloatDesk.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace FloatDesk.Windows.Network;

/// <summary>Privileged, fixed-purpose helper. No arbitrary paths, hosts, commands or scripts are accepted.</summary>
public static class SteamHostsHelper
{
    internal const string PipePrefix = "YueYuSteamHosts-";
    public static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    public static int Run(string[] arguments)
    {
        if (arguments.Length != 5 || arguments[0] != "--steam-hosts-helper" ||
            !arguments[1].StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(arguments[1][PipePrefix.Length..], "N", out var owner) ||
            !int.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
            !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mask) || mask is < 0 or > 7) return 2;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        using var parent = Process.GetProcessById(pid);
        using var self = Process.GetCurrentProcess();
        if (parent.StartTime.ToUniversalTime().Ticks != ticks || parent.SessionId != self.SessionId ||
            !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 4;
        using var pipe = new NamedPipeClientStream(".", arguments[1], PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        pipe.Connect(30000);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != pid) return 5;
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        using var mutex = new Mutex(false, "Global\\YueYuOrb.SteamHostsLease");
        var held = false;
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) { writer.WriteLine("ERROR:另一个月语 hosts 助手仍在运行。"); return 6; }
            UpdateHosts(RemoveStaleLeases);
            if (mask != 0)
            {
                var settings = new SteamNetworkSettings { Store = (mask & 1) != 0, Community = (mask & 2) != 0, Content = (mask & 4) != 0 };
                UpdateHosts(bytes => SteamHostsPatch.Add(bytes, settings, owner, pid, ticks));
            }
            writer.WriteLine("READY");
            var command = reader.ReadLineAsync();
            while (!command.Wait(250)) { if (parent.HasExited) break; }
            // EOF, STOP, or process exit all mean the lease has ended.
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            try { writer.WriteLine("ERROR:" + error.GetType().Name + "：" + error.Message); } catch (IOException) { }
            return 7;
        }
        finally
        {
            if (held)
            {
                var restored = false;
                for (var attempt = 0; attempt < 3 && !restored; attempt++)
                {
                    try { UpdateHosts(bytes => SteamHostsPatch.Remove(bytes, owner)); restored = true; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Thread.Sleep(200); }
                }
                try { writer.WriteLine(restored ? "RESTORED" : "ERROR:hosts 尚未恢复，请使用恢复网络配置。"); } catch (IOException) { }
                mutex.ReleaseMutex();
            }
        }
    }

    internal static byte[] RemoveStaleLeases(byte[] bytes)
    {
        foreach (var lease in SteamHostsPatch.Owners(bytes))
        {
            var alive = false;
            try { using var process = Process.GetProcessById(lease.ProcessId); alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == lease.Ticks; }
            catch (ArgumentException) { }
            if (alive) throw new IOException("hosts 租约的主进程仍在运行，不会接管。");
            bytes = SteamHostsPatch.Remove(bytes, lease.Owner);
        }
        return bytes;
    }

    internal static void UpdateHosts(Func<byte[], byte[]> transform)
    {
        using var original = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (original.Length > 1024 * 1024) throw new IOException("hosts 文件过大，未作修改。");
        var bytes = new byte[(int)original.Length]; original.ReadExactly(bytes);
        var updated = transform(bytes);
        if (updated.AsSpan().SequenceEqual(bytes)) return;
        var temporary = HostsPath + ".YueYu-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, updated);
            if (!File.ReadAllBytes(HostsPath).AsSpan().SequenceEqual(bytes)) throw new IOException("hosts 已被其他程序修改，请重试。");
            File.Replace(temporary, HostsPath, null);
            DnsFlushResolverCache();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
    [DllImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
