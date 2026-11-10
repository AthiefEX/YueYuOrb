using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace FloatDesk.Windows.DesktopSessions;

public sealed class GuestCredential(string userName, SecureString password) : IDisposable
{
    public string UserName { get; } = userName;
    public SecureString Password { get; } = password;
    public void Dispose() => Password.Dispose();
    public override string ToString() => "Protected guest credential";
}

public interface IGuestCredentialStore
{
    void Save(Guid vmId, string userName, SecureString password);
    GuestCredential? Load(Guid vmId);
    void Delete(Guid vmId);
}

public sealed class WindowsGuestCredentialStore : IGuestCredentialStore
{
    private static string Target(Guid vmId) => vmId != Guid.Empty ? $"YueYuOrb/HyperV/Guest/{vmId:D}" : throw new ArgumentException("VM identity required.", nameof(vmId));

    public void Save(Guid vmId, string userName, SecureString password)
    {
        ValidateCredential(userName, password);
        Write(Target(vmId), userName, password, "YueYuOrb protected per-VM guest credential", 2);
    }

    public static void ValidateCredential(string userName, SecureString password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 64 ||
            userName.Any(c => char.IsControl(c) || "\\/@\"[]:;|=,+*?<>".Contains(c)) || userName != userName.Trim() || password.Length is < 1 or > 256)
            throw new ArgumentException("请填写有效的来宾本地用户名和非空密码（不含域名）。");
    }

    private static void Write(string target, string userName, SecureString password, string comment, uint persist)
    {
        var blob = Marshal.SecureStringToCoTaskMemUnicode(password);
        try
        {
            var native = new NativeCredential { Type = 1, TargetName = target, UserName = userName,
                CredentialBlob = blob, CredentialBlobSize = checked((uint)password.Length * 2), Persist = persist,
                Comment = comment };
            if (!CredWrite(ref native, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    private static void FreeSecret(nint pointer)
    {
        var native = Marshal.PtrToStructure<NativeCredential>(pointer);
        if (native.CredentialBlob != 0 && native.CredentialBlobSize <= 512)
            for (var i = 0; i < native.CredentialBlobSize; i++) Marshal.WriteByte(native.CredentialBlob, i, 0);
        CredFree(pointer);
    }

    public GuestCredential? Load(Guid vmId)
    {
        if (!CredRead(Target(vmId), 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlobSize is 0 or > 512 || native.CredentialBlobSize % 2 != 0 || native.UserName is null)
                throw new InvalidOperationException("保存的来宾凭据无效，请重新设置。");
            var password = new SecureString();
            try
            {
                for (var i = 0; i < native.CredentialBlobSize / 2; i++) password.AppendChar((char)Marshal.ReadInt16(native.CredentialBlob, i * 2));
                password.MakeReadOnly();
                return new GuestCredential(native.UserName, password);
            }
            catch { password.Dispose(); throw; }
        }
        finally { FreeSecret(pointer); }
    }

    public void Delete(Guid vmId)
    {
        if (!CredDelete(Target(vmId), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
