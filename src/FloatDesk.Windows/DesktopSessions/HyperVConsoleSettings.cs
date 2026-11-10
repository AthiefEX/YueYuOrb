using FloatDesk.Core.Models;

namespace FloatDesk.Windows.DesktopSessions;

public static class HyperVConsoleSettings
{
    public static string BuildDisplayScript(Guid vmId, int width, int height, IReadOnlyList<string>? sharedDrives = null)
    {
        if (vmId == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(vmId));
        if (width is < 800 or > 3840 || height is < 600 or > 2160) throw new ArgumentOutOfRangeException(nameof(width));
        sharedDrives ??= [];
        if (sharedDrives.Any(drive => drive.Length != 3 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':' || drive[2] != '\\'))
            throw new ArgumentException("Only explicit local drive roots may be redirected.", nameof(sharedDrives));
        // VMConnect expects bare, lowercase drive letters (for example "f;z").
        var drives = string.Join(';', sharedDrives
            .Select(drive => char.ToLowerInvariant(drive[0]))
            .Distinct());
        return $$"""
            $ErrorActionPreference='Stop'
            [Reflection.Assembly]::Load('Microsoft.Virtualization.Client.Management, Version=10.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35') | Out-Null
            $assembly=[Reflection.Assembly]::Load('Microsoft.Virtualization.Client, Version=10.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35')
            $server=[Microsoft.Virtualization.Client.Management.Server]::GetServer('localhost')
            $vm=[Microsoft.Virtualization.Client.Management.ObjectLocator]::GetVMComputerSystem($server,'{{vmId:D}}')
            $options=[Activator]::CreateInstance($assembly.GetType('Microsoft.Virtualization.Client.RdpOptions'),[object[]]@($vm))
            $options.DesktopSize=New-Object Drawing.Size({{width}},{{height}})
            $options.UseAllMonitors=$false
            $options.FullScreen=$false
            $options.RedirectedDrives='{{drives}}'
            $options.Save()
            'ok'
            """;
    }

    public static string BuildCloseScript(Guid vmId, HyperVConsoleCloseAction action)
    {
        if (vmId == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(vmId));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (action == HyperVConsoleCloseAction.CloseView) return "'ok'";
        var operation = action == HyperVConsoleCloseAction.SaveState
            ? "if ($vm.State -ne 'Saved' -and $vm.State -ne 'Off') { Stop-VM -VM $vm -Save -Force -Confirm:$false -ErrorAction Stop }"
            : "if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force -Confirm:$false -ErrorAction Stop }";
        // Read the state back: a Hyper-V host can accept the command and still leave the machine
        // running (a pending checkpoint, a guest that refuses the save, a lost CIM call). Reporting
        // "ok" here is what made the UI claim success while memory was never released. Stop-VM also
        // returns before the save finishes, so the state is polled until it settles.
        var expected = action == HyperVConsoleCloseAction.SaveState ? "Saved" : "Off";
        return $$"""
            $ErrorActionPreference='Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm=Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            {{operation}}
            $deadline=(Get-Date).AddSeconds(90)
            do {
                $after=(Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop).State
                if ($after -eq '{{expected}}' -or $after -eq 'Off') { break }
                Start-Sleep -Milliseconds 500
            } while ((Get-Date) -lt $deadline)
            if ($after -ne '{{expected}}' -and $after -ne 'Off') { throw ('VMStateReadbackMismatch:' + $after) }
            'ok'
            """;
    }
}
