using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FloatDesk.Windows.DesktopSessions;

public sealed partial class WindowsChildSessionAgentLauncher
{
    private async Task<uint> ProbeParentChildSessionAsync(SessionPeerIdentity caller, CancellationToken cancellationToken)
    {
        var pipeName = "YueYuOrb.ParentProbe." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(caller.UserSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var pipe = NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            4096, 4096, security);
        var rawToken = await QueryUserTokenWithRetryAsync(caller.SessionId, cancellationToken).ConfigureAwait(false);
        using var token = new SafeAccessTokenHandle(rawToken);
        if (!string.Equals(GetTokenUserSid(token), caller.UserSid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ParentProbeUserMismatch");
        var commandLine = Marshal.StringToHGlobalUni(Quote(_agentPath) + " --probe-child " + Quote(pipeName));
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\default" };
        IntPtr environment = IntPtr.Zero;
        ProcessInformation process;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
                throw Win32("ParentProbeEnvironment");
            if (!CreateProcessAsUser(token, _agentPath, commandLine, IntPtr.Zero, IntPtr.Zero,
                    false, CreateUnicodeEnvironment | CreateNoWindow, environment, _workingDirectory,
                    ref startup, out process))
                throw Win32("ParentProbeLaunch");
        }
        finally
        {
            Marshal.FreeHGlobal(commandLine);
            if (environment != IntPtr.Zero)
                DestroyEnvironmentBlock(environment);
        }
        using var processHandle = new SafeProcessHandle(process.ProcessHandle, true);
        using var threadHandle = new SafeFileHandle(process.ThreadHandle, true);
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            var peer = NamedPipePeerIdentity.GetClientIdentity(pipe);
            if (peer.ProcessId != process.ProcessId || peer.SessionId != caller.SessionId ||
                !string.Equals(peer.UserSid, caller.UserSid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ParentProbePeerMismatch");
            var bytes = new byte[4];
            await pipe.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        finally
        {
            _ = TerminateProcess(processHandle, 0);
        }
    }
}
