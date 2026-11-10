using FloatDesk.Core.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FloatDesk.Windows.DesktopSessions;

public static class AutomationClientIdentity
{
    public static AutomationClientTrust Resolve(AutomationCaller caller,string name)
    {
        using var process=Process.GetProcessById(checked((int)caller.ProcessId));
        if((uint)process.SessionId!=caller.SessionId || Math.Abs((process.StartTime.ToUniversalTime()-caller.StartedAtUtc.UtcDateTime).TotalMilliseconds)>1)
            throw new InvalidOperationException("ClientProcessChanged");
        if(NtQueryInformationProcess(process.Handle,0,out var info,Marshal.SizeOf<ProcessBasicInformation>(),out _)!=0)
            throw new InvalidOperationException("ClientLauncherUnavailable");
        using var parent=Process.GetProcessById(checked((int)info.ParentId));
        if(parent.SessionId!=process.SessionId || parent.StartTime>process.StartTime)throw new InvalidOperationException("ClientLauncherChanged");
        var path=parent.MainModule?.FileName??throw new InvalidOperationException("ClientLauncherUnavailable");
        using var file=File.OpenRead(path);
        var hash=Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()+":"+hash+":"+name.ToUpperInvariant()))).ToLowerInvariant();
        return new(id,name,path,hash,false,DateTimeOffset.UtcNow);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation
    { public IntPtr Reserved1,PebBaseAddress,Reserved2A,Reserved2B,ProcessId,ParentId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process,int type,out ProcessBasicInformation information,int size,out int returned);
}
