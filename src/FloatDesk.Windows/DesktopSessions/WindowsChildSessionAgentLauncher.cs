using FloatDesk.Core.Models;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace FloatDesk.Windows.DesktopSessions;

public sealed partial class WindowsChildSessionAgentLauncher :
    ISessionBrokerAgentLauncher
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenStatisticsInformation = 10;
    private const uint TokenUserInformation = 1;
    private const int WtsInfo = 24;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint InvalidSessionId = uint.MaxValue;

    private readonly string _agentPath;
    private readonly string _workingDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly string _bootId;

    public WindowsChildSessionAgentLauncher(
        string? brokerDirectory = null,
        string? agentPath = null,
        TimeProvider? timeProvider = null)
    {
        var directory = Path.GetFullPath(
            brokerDirectory ?? AppContext.BaseDirectory);
        _agentPath = Path.GetFullPath(agentPath ?? Path.Combine(
            directory,
            "..",
            "agent",
            "YueYuOrb.SessionAgent.exe"));
        _workingDirectory = Path.GetDirectoryName(_agentPath) ??
            throw new InvalidOperationException(
                "The session agent directory is invalid.");
        _timeProvider = timeProvider ?? TimeProvider.System;
        var boot = _timeProvider.GetUtcNow() -
            TimeSpan.FromMilliseconds(Environment.TickCount64);
        _bootId = new DateTimeOffset(
            boot.UtcTicks - boot.UtcTicks % TimeSpan.FromMinutes(5).Ticks,
            TimeSpan.Zero).ToString("O");
    }

    public async Task<SessionBrokerResponse> LaunchAsync(
        SessionBrokerRequest request,
        SessionPeerIdentity caller,
        CancellationToken cancellationToken)
    {
        SessionBrokerProtocol.ValidateRequest(request);
        ArgumentNullException.ThrowIfNull(caller);
        await VerifyAgentManifestAsync(cancellationToken).ConfigureAwait(false);
        EnableRequiredPrivilege("SeTcbPrivilege");
        EnableRequiredPrivilege("SeAssignPrimaryTokenPrivilege");
        EnableRequiredPrivilege("SeIncreaseQuotaPrivilege");
        await ValidateChildSessionAsync(request, caller, cancellationToken).ConfigureAwait(false);

        var rawToken = await QueryUserTokenWithRetryAsync(
            request.WindowsSessionId,
            cancellationToken).ConfigureAwait(false);

        using var token = new SafeAccessTokenHandle(rawToken);
        var userSid = GetTokenUserSid(token);
        if (!string.Equals(
                userSid,
                caller.UserSid,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The child-session user does not match the broker caller.");
        }

        var authenticationId = GetAuthenticationId(token);
        var loginTime = GetSessionLoginTime(request.WindowsSessionId);
        var loginInstanceId = LoginInstanceIdentity.Create(
            new LoginIdentitySource(
                _bootId,
                userSid,
                authenticationId,
                loginTime));

        var secret = RandomNumberGenerator.GetBytes(
            SessionProtocol.BootstrapSecretSize);
        var pipeAttributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        CreatePipe(
            out var secretRead,
            out var secretWrite,
            ref pipeAttributes,
            0);
        using var readHandle = new SafeFileHandle(secretRead, ownsHandle: true);
        using var writeHandle = new SafeFileHandle(secretWrite, ownsHandle: true);
        if (!SetHandleInformation(
                writeHandle,
                HandleFlagInherit,
                0))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw Win32("SetHandleInformation");
        }

        using var nullHandle = CreateInheritableNullHandle();
        var commandLine = BuildCommandLine(
            request,
            caller.SessionId,
            userSid,
            loginInstanceId);
        var commandLineBuffer = Marshal.StringToHGlobalUni(commandLine);
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = StartfUseStdHandles,
            StandardInput = readHandle.DangerousGetHandle(),
            StandardOutput = nullHandle.DangerousGetHandle(),
            StandardError = nullHandle.DangerousGetHandle(),
            Desktop = "winsta0\\default"
        };
        IntPtr environment = IntPtr.Zero;
        ProcessInformation process;
        try
        {
            if (!CreateEnvironmentBlock(
                    out environment,
                    token,
                    false))
            {
                throw Win32("CreateEnvironmentBlock");
            }

            if (!CreateProcessAsUser(
                    token,
                    _agentPath,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateUnicodeEnvironment | CreateNoWindow,
                    environment,
                    _workingDirectory,
                    ref startup,
                    out process))
            {
                throw Win32("CreateProcessAsUser");
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(commandLineBuffer);
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
        }

        using var processHandle = new SafeProcessHandle(
            process.ProcessHandle,
            ownsHandle: true);
        using var threadHandle = new SafeFileHandle(
            process.ThreadHandle,
            ownsHandle: true);
        try
        {
            var encoded = Convert.ToBase64String(secret) + Environment.NewLine;
            var bytes = Encoding.ASCII.GetBytes(encoded);
            using var secretStream = new FileStream(
                writeHandle,
                FileAccess.Write);
            await secretStream.WriteAsync(
                bytes,
                cancellationToken).ConfigureAwait(false);
            await secretStream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!GetProcessTimes(
                    processHandle,
                    out var creation,
                    out _,
                    out _,
                    out _))
            {
                throw Win32("GetProcessTimes");
            }

            return new SessionBrokerResponse(
                SessionBrokerProtocol.CurrentVersion,
                true,
                loginInstanceId,
                userSid,
                process.ProcessId,
                DateTimeOffset.FromFileTime(
                    ((long)creation.HighDateTime << 32) |
                    creation.LowDateTime),
                secret,
                null);
        }
        catch
        {
            _ = TerminateProcess(processHandle, 1);
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }
    }

    private async Task VerifyAgentManifestAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_agentPath))
            throw new InvalidOperationException("SessionAgentMissing");
        var manifestPath = _agentPath + ".sha256";
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("SessionAgentManifestMissing");
        var manifest = (await File.ReadAllTextAsync(
            manifestPath,
            cancellationToken).ConfigureAwait(false)).Trim();
        var expected = manifest.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null ||
            expected.Length != 64 ||
            expected.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("SessionAgentManifestInvalid");
        }

        await using var stream = new FileStream(
            _agentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var actual = await SHA256.HashDataAsync(
            stream,
            cancellationToken).ConfigureAwait(false);
        var actualText = Convert.ToHexString(actual);
        if (!string.Equals(
                expected,
                actualText,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "SessionAgentManifestMismatch");
        }
    }

    private async Task ValidateChildSessionAsync(
        SessionBrokerRequest request,
        SessionPeerIdentity caller,
        CancellationToken cancellationToken)
    {
        var activeConsole = WTSGetActiveConsoleSessionId();
        if (activeConsole == InvalidSessionId ||
            activeConsole != caller.SessionId)
        {
            throw new InvalidOperationException(
                "BrokerCallerNotActiveConsole");
        }

        uint childSessionId;
        try
        {
            childSessionId = await ProbeParentChildSessionAsync(caller, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException("ParentSessionProbeTimedOut", exception);
        }
        if (childSessionId != request.WindowsSessionId ||
            childSessionId == caller.SessionId)
        {
            throw new InvalidOperationException(
                "BrokerChildSessionMismatch");
        }
    }

    private async Task<IntPtr> QueryUserTokenWithRetryAsync(
        uint sessionId,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() +
            TimeSpan.FromSeconds(30);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WTSQueryUserToken(sessionId, out var token))
                return token;
            if (_timeProvider.GetUtcNow() >= deadline)
                throw Win32("WTSQueryUserToken");
            await Task.Delay(
                TimeSpan.FromMilliseconds(250),
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetTokenUserSid(SafeAccessTokenHandle token)
    {
        _ = GetTokenInformation(
            token,
            TokenUserInformation,
            IntPtr.Zero,
            0,
            out var required);
        if (required == 0)
            throw Win32("GetTokenInformation.TokenUser.Size");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetTokenInformation(
                    token,
                    TokenUserInformation,
                    buffer,
                    required,
                    out _))
            {
                throw Win32("GetTokenInformation.TokenUser");
            }

            var user = Marshal.PtrToStructure<TokenUser>(buffer);
            return new SecurityIdentifier(user.User.Sid).Value;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string GetAuthenticationId(SafeAccessTokenHandle token)
    {
        var size = Marshal.SizeOf<TokenStatistics>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(
                    token,
                    TokenStatisticsInformation,
                    buffer,
                    checked((uint)size),
                    out _))
            {
                throw Win32("GetTokenInformation.TokenStatistics");
            }

            var statistics = Marshal.PtrToStructure<TokenStatistics>(buffer);
            return $"{statistics.AuthenticationId.HighPart:X8}:{statistics.AuthenticationId.LowPart:X8}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static DateTimeOffset GetSessionLoginTime(uint sessionId)
    {
        if (!WTSQuerySessionInformation(
                IntPtr.Zero,
                sessionId,
                WtsInfo,
                out var buffer,
                out _))
        {
            throw Win32("WTSQuerySessionInformation");
        }

        try
        {
            var info = Marshal.PtrToStructure<WtsInfoData>(buffer);
            if (info.LogonTime <= 0)
                throw new InvalidOperationException(
                    "BrokerSessionLoginTimeUnavailable");
            return DateTimeOffset.FromFileTime(info.LogonTime);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static void EnableRequiredPrivilege(string name)
    {
        if (!OpenProcessToken(
                GetCurrentProcess(),
                TokenAdjustPrivileges | TokenQuery,
                out var rawToken))
        {
            throw Win32("OpenProcessToken");
        }

        using var token = new SafeAccessTokenHandle(rawToken);
        if (!LookupPrivilegeValue(
                null,
                name,
                out var localId))
        {
            throw Win32($"LookupPrivilegeValue.{name}");
        }

        var privileges = new TokenPrivileges
        {
            PrivilegeCount = 1,
            Luid = localId,
            Attributes = 0x00000002
        };
        if (!AdjustTokenPrivileges(
                token,
                false,
                ref privileges,
                0,
                IntPtr.Zero,
                IntPtr.Zero) ||
            Marshal.GetLastWin32Error() != 0)
        {
            throw Win32($"AdjustTokenPrivileges.{name}");
        }
    }

    private static SafeFileHandle CreateInheritableNullHandle()
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true
        };
        var handle = CreateFile(
            "NUL",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            ref attributes,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == IntPtr.Zero ||
            handle == new IntPtr(-1))
        {
            throw Win32("CreateFile.NUL");
        }

        return new SafeFileHandle(handle, ownsHandle: true);
    }

    private string BuildCommandLine(
        SessionBrokerRequest request,
        uint hostWindowsSessionId,
        string userSid,
        Guid loginInstanceId)
    {
        var arguments = new[]
        {
            "--pipe", SessionProtocol.GetPipeName(request.EnvironmentId),
            "--environment", request.EnvironmentId.ToString("D"),
            "--generation", request.Generation.ToString("D"),
            "--login", loginInstanceId.ToString("D"),
            "--session", request.WindowsSessionId.ToString(
                CultureInfo.InvariantCulture),
            "--host-session", hostWindowsSessionId.ToString(
                CultureInfo.InvariantCulture),
            "--user-sid", userSid
        };
        var values = new List<string>(arguments.Length + 1)
        {
            Quote(_agentPath)
        };
        for (var index = 0; index < arguments.Length; index += 2)
        {
            values.Add(Quote(arguments[index]));
            values.Add(Quote(arguments[index + 1]));
        }

        return string.Join(' ', values);
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static Win32Exception Win32(string operation) =>
        new(Marshal.GetLastWin32Error(), operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUser
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoData
    {
        public int State;
        public uint SessionId;
        public uint IncomingBytes;
        public uint OutgoingBytes;
        public uint IncomingFrames;
        public uint OutgoingFrames;
        public uint IncomingCompressedBytes;
        public uint OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)]
        public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)]
        public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(
        uint sessionId,
        out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport(
        "wtsapi32.dll",
        EntryPoint = "WTSQuerySessionInformationW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        int infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(
        IntPtr process,
        uint access,
        out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        uint informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "LookupPrivilegeValueW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(
        string? systemName,
        string name,
        out Luid localId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr environment,
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CreateProcessAsUserW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(
        SafeAccessTokenHandle token,
        string applicationName,
        IntPtr commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle process,
        out FileTime creationTime,
        out FileTime exitTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        SafeProcessHandle process,
        uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
