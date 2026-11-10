using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FloatDesk.Windows.Network;

public static class SteamBackgroundInstallation
{
    public static string LastStage { get; private set; } = "begin";
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "YueYuOrb", "steam-network");
    internal static string ConfigurationPath => Path.Combine(DirectoryPath, "network-service.json");
    internal sealed record Registration(string Sid, Dictionary<string, string> ClientHashes);

    public static int Configure(bool install, string sid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new UnauthorizedAccessException();
        _ = new SecurityIdentifier(sid);
        if (install)
        {
            using var installLease = new Mutex(false, "Global\\YueYuOrb.SteamHostsLease");
            var held = false;
            try
            {
            try { held = installLease.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("仍有活动加速租约，请先停止加速。");
            LastStage = "stop-service";
            if (string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("请从原发布目录安装或修复。");
            if (RunningProcessId() != 0) _ = TrySc("stop", SteamBackgroundProtocol.ServiceName);
            WaitStopped();
            LastStage = "protect-directory";
            var folder = System.IO.Directory.CreateDirectory(DirectoryPath);
            if ((folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("服务目录不能是链接。");
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var account in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(acl);
            LastStage = "copy-payload";
            foreach (var file in System.IO.Directory.EnumerateFiles(AppContext.BaseDirectory))
                if (Path.GetExtension(file) is ".exe" or ".dll" || file.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                    CopyWithRetry(file, Path.Combine(DirectoryPath, Path.GetFileName(file)));
            var hashes = new Dictionary<string, string>();
            LastStage = "register-client";
            foreach (var name in new[] { "YueYuOrb.exe", "YueYuOrb.dll", "FloatDesk.Windows.dll" })
                hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
            File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new Registration(sid, hashes)));
            var exists = TrySc("query", SteamBackgroundProtocol.ServiceName);
            LastStage = "configure-service";
            RunSc(exists ? "config" : "create", SteamBackgroundProtocol.ServiceName, "binPath=", "\"" + Path.Combine(DirectoryPath, "YueYuOrb.exe") + "\" --steam-background-service", "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "月语 Steam 网络后台");
            RunSc("failure", SteamBackgroundProtocol.ServiceName, "reset=", "86400", "actions=", "restart/3000/restart/10000/restart/30000");
            LastStage = "start-service";
            RunSc("start", SteamBackgroundProtocol.ServiceName);
            }
            finally { if (held) installLease.ReleaseMutex(); }
        }
        else
        {
            // The exclusive hosts mutex prevents uninstalling underneath any active lease.
            using var mutex = new Mutex(false, "Global\\YueYuOrb.SteamHostsLease");
            var held = false;
            try
            {
                try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                if (!held) throw new IOException("仍有活动加速租约，请先停止加速。");
                if (RunningProcessId() != 0) _ = TrySc("stop", SteamBackgroundProtocol.ServiceName);
                WaitStopped();
                SteamBackgroundServer.RecoverOutstandingLease();
                SteamBackgroundAuthority.Uninstall();
                if (TrySc("query", SteamBackgroundProtocol.ServiceName)) RunSc("delete", SteamBackgroundProtocol.ServiceName);
                if (System.IO.Directory.Exists(DirectoryPath))
                {
                    if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("服务目录不允许是链接。");
                    foreach (var file in System.IO.Directory.EnumerateFiles(DirectoryPath)) File.Delete(file);
                    System.IO.Directory.Delete(DirectoryPath);
                }
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
        return 0;
    }

    private static void WaitStopped()
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (RunningProcessId() != 0)
        {
            if (DateTime.UtcNow >= until) throw new IOException("网络后台未停止。");
            Thread.Sleep(200);
        }
    }

    internal static bool VerifyClient(int pid, Registration registration)
    {
        if (!ClientSidMatches(pid, registration.Sid)) return false;
        using var process = Process.GetProcessById(pid);
        var path = process.MainModule?.FileName;
        if (path is null || process.HasExited || Path.GetFileName(path) != "YueYuOrb.exe") return false;
        foreach (var pair in registration.ClientHashes)
        {
            var file = Path.Combine(Path.GetDirectoryName(path)!, pair.Key);
            if (!File.Exists(file)) return false;
            using var stream = File.OpenRead(file);
            if (stream.Length > 128 * 1024 * 1024 || !string.Equals(Convert.ToHexString(SHA256.HashData(stream)), pair.Value, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    internal static bool ClientSidMatches(int pid, string sid)
    {
        using var process = Process.GetProcessById(pid);
        if (!OpenProcessToken(process.Handle, 8, out var token)) return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value == sid;
    }

    public static uint RunningProcessId()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return 0;
        try
        {
            var service = OpenService(manager, SteamBackgroundProtocol.ServiceName, 4);
            if (service == IntPtr.Zero) return 0;
            try { return QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceProcessStatus>(), out _) && status.State != 1 ? status.Pid : 0; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static void RunSc(params string[] arguments) { if (!TrySc(arguments)) throw new IOException("Windows 网络服务配置失败。"); }
    private static void CopyWithRetry(string source, string destination)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (true)
            try { File.Copy(source, destination, true); return; }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(200); }
    }
    private static bool TrySc(params string[] arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("服务管理器不可用。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(20000)) throw new IOException("服务配置超时。");
        Task.WhenAll(output, error).GetAwaiter().GetResult();
        return process.ExitCode == 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct ServiceProcessStatus { public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, Wait, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceProcessStatus status, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}
