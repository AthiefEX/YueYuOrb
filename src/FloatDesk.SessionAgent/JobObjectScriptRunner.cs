using FloatDesk.Core.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FloatDesk.SessionAgent;

public sealed class JobObjectScriptRunner :
    ISessionScriptRunner,
    IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, OwnedScript> _active = [];
    private bool _disposed;

    public async Task<CommandResult> RunAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != SessionCommandKind.RunScript ||
            !RunScriptCommandCodec.TryDecode(
                command.Parameters,
                out var scriptPath,
                out var arguments) ||
            scriptPath is null ||
            !File.Exists(scriptPath))
        {
            return Failed(command, "ScriptPathMissing");
        }

        var extension = Path.GetExtension(scriptPath);
        ProcessStartInfo startInfo;
        if (string.Equals(
                extension,
                ".ps1",
                StringComparison.OrdinalIgnoreCase))
        {
            var powerShell = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            if (!File.Exists(powerShell))
                return Failed(command, "PowerShellUnavailable");
            startInfo = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ??
                    Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }
        else if (string.Equals(
                     extension,
                     ".exe",
                     StringComparison.OrdinalIgnoreCase))
        {
            startInfo = new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ??
                    Environment.CurrentDirectory
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }
        else
        {
            return Failed(command, "ScriptTypeUnsupported");
        }

        Process? process = null;
        OwnedScript? owned = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return Failed(command, "ScriptLaunchFailed");
            var job = CreateKillOnCloseJob();
            if (job == IntPtr.Zero)
            {
                KillOwnedProcess(process);
                return Failed(command, "ScriptJobCreationFailed");
            }
            if (!AssignProcessToJobObject(job, process.Handle))
            {
                CloseHandle(job);
                KillOwnedProcess(process);
                return Failed(command, "ScriptJobAssignmentFailed");
            }

            owned = new OwnedScript(
                command.CommandId,
                process,
                job,
                cancellationToken);
            lock (_sync)
            {
                if (!_active.TryAdd(command.CommandId, owned))
                {
                    owned.Close();
                    return Failed(command, "DuplicateScriptCommand");
                }
            }

            try
            {
                await process.WaitForExitAsync(owned.Token)
                    .ConfigureAwait(false);
                return process.ExitCode == 0
                    ? Succeeded(command)
                    : Failed(command, "ScriptExitedWithError");
            }
            catch (OperationCanceledException)
                when (owned.Token.IsCancellationRequested)
            {
                owned.Terminate();
                return new CommandResult(
                    command.CommandId,
                    false,
                    true,
                    "OutcomeUnknown");
            }
            finally
            {
                lock (_sync)
                    _active.Remove(command.CommandId);
                owned.Close();
            }
        }
        catch
        {
            owned?.Close();
            if (process is not null)
                KillOwnedProcess(process);

            return Failed(command, "ScriptLaunchFailed");
        }
        finally
        {
            process?.Dispose();
        }
    }

    public Task ReleaseOwnedProcessesAsync(
        CancellationToken cancellationToken)
    {
        OwnedScript[] owned;
        lock (_sync)
            owned = _active.Values.ToArray();
        foreach (var script in owned)
        {
            script.Terminate();
            script.Cancel();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var script in _active.Values.ToArray())
        {
            script.Terminate();
            script.Cancel();
            script.Close();
        }

        _active.Clear();
    }

    private static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;
        var information = new JobObjectExtendedLimitInformationNative
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformation,
                ref information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationNative>()))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    private static CommandResult Succeeded(SessionCommand command) =>
        new(command.CommandId, true, false, null);

    private static CommandResult Failed(
        SessionCommand command,
        string errorCode) =>
        new(command.CommandId, false, false, errorCode);

    private static void KillOwnedProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed class OwnedScript : IDisposable
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation;
        private IntPtr _job;
        private bool _closed;

        public OwnedScript(
            Guid commandId,
            Process process,
            IntPtr job,
            CancellationToken cancellationToken)
        {
            CommandId = commandId;
            Process = process;
            _job = job;
            _cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
        }

        public Guid CommandId { get; }
        public Process Process { get; }
        public CancellationToken Token => _cancellation.Token;

        public bool Terminate()
        {
            lock (_sync)
            {
                return _job != IntPtr.Zero &&
                    TerminateJobObject(_job, 1);
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Close()
        {
            IntPtr job;
            lock (_sync)
            {
                if (_closed)
                    return;
                _closed = true;
                job = _job;
                _job = IntPtr.Zero;
            }

            if (job != IntPtr.Zero)
                CloseHandle(job);
            _cancellation.Dispose();
        }

        public void Dispose() => Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateJobObject(
        IntPtr securityAttributes,
        string? name);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "SetInformationJobObject",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref JobObjectExtendedLimitInformationNative information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        IntPtr job,
        IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        IntPtr job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
