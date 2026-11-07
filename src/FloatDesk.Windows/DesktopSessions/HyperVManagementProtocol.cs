using FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.Windows.DesktopSessions;

public enum HyperVManagementOperation
{
    Inspect,
    Start,
    Stop,
    ApplyResources
}

public sealed record HyperVManagementRequest(
    Guid EnvironmentId,
    Guid VmId,
    HyperVManagementOperation Operation,
    VmResourceProfile? Resources,
    IReadOnlySet<Guid> RegisteredVmIds);

public static class HyperVManagementProtocol
{
    public static void Validate(HyperVManagementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentId == Guid.Empty || request.VmId == Guid.Empty)
            throw new InvalidOperationException("HyperVIdentityRequired");
        if (request.RegisteredVmIds is null || !request.RegisteredVmIds.Contains(request.VmId))
            throw new InvalidOperationException("HyperVVmNotRegistered");
        if (!Enum.IsDefined(request.Operation))
            throw new InvalidOperationException("HyperVOperationInvalid");
        if (request.Operation == HyperVManagementOperation.ApplyResources && request.Resources is null)
            throw new InvalidOperationException("HyperVResourcesRequired");
        if (request.Resources is not null && request.Resources.EnvironmentId != request.EnvironmentId)
            throw new InvalidOperationException("HyperVResourceEnvironmentMismatch");
    }

    public static string BuildResourceScript(Guid vmId, VmResourceProfile resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var request = new HyperVManagementRequest(
            resources.EnvironmentId, vmId, HyperVManagementOperation.ApplyResources,
            resources, new HashSet<Guid> { vmId });
        Validate(request);
        if (resources.CpuCount < 2 || resources.MemoryStartupMiB <= 0 || resources.DiskSizeGiB <= 0)
            throw new InvalidOperationException("HyperVResourcesInvalid");
        var id = vmId.ToString("D");
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{id}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') { throw 'VmMustBeOffForResourceChange' }
            Set-VM -VM $vm -ProcessorCount {{resources.CpuCount}} -MemoryStartupBytes {{resources.MemoryStartupMiB}}MB -ErrorAction Stop
            [pscustomobject]@{ applied = $true; vmId = $vm.Id } | ConvertTo-Json -Compress
            """;
    }
}
