using FloatDesk.Core.Models.DesktopAutomation;
using System.Globalization;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public interface IHyperVEnvironmentManager
{
    Task<VmResourceProfile> InspectAsync(Guid environmentId, Guid vmId, CancellationToken cancellationToken);
    Task ApplyAsync(Guid environmentId, Guid vmId, VmResourceProfile profile, CancellationToken cancellationToken);
}

public sealed class PowerShellHyperVEnvironmentManager : IHyperVEnvironmentManager
{
    private readonly PowerShellHyperVHost _host;

    public PowerShellHyperVEnvironmentManager(PowerShellHyperVHost? host = null) =>
        _host = host ?? new PowerShellHyperVHost();

    public async Task<VmResourceProfile> InspectAsync(Guid environmentId, Guid vmId, CancellationToken cancellationToken)
    {
        if (environmentId == Guid.Empty || vmId == Guid.Empty)
            throw new ArgumentException("Environment and VM identities are required.");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            $memory = [int64]($vm.MemoryStartup / 1MB)
            [pscustomobject]@{
              cpu = [int]$vm.ProcessorCount
              memoryStartupMiB = $memory
              memoryMinimumMiB = $memory
              memoryMaximumMiB = $memory
              diskSizeGiB = [int64]80
              storagePath = ''
              networkSwitch = ''
              dynamicMemory = [bool]$vm.DynamicMemoryEnabled
              gpuMode = 'standard'
            } | ConvertTo-Json -Compress
            """;
        var json = await _host.RunScriptForManagementAsync(script, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new VmResourceProfile(
            environmentId,
            root.GetProperty("cpu").GetInt32(),
            root.GetProperty("memoryStartupMiB").GetInt64(),
            root.GetProperty("memoryMinimumMiB").GetInt64(),
            root.GetProperty("memoryMaximumMiB").GetInt64(),
            root.GetProperty("diskSizeGiB").GetInt64(),
            root.GetProperty("storagePath").GetString() ?? string.Empty,
            root.GetProperty("networkSwitch").GetString() ?? string.Empty,
            root.GetProperty("dynamicMemory").GetBoolean(),
            root.GetProperty("gpuMode").GetString() ?? "standard");
    }

    public Task ApplyAsync(Guid environmentId, Guid vmId, VmResourceProfile profile, CancellationToken cancellationToken)
    {
        if (profile.EnvironmentId != environmentId)
            throw new InvalidOperationException("HyperVResourceEnvironmentMismatch");
        var validation = VmResourceProfileValidator.Validate(profile, new HostResourceCapacity(
            Environment.ProcessorCount, long.MaxValue, long.MaxValue));
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        return _host.RunScriptForManagementAsync(
            HyperVManagementProtocol.BuildResourceScript(vmId, profile), cancellationToken);
    }
}

public static class HyperVManagementScriptExtensions
{
    public static Task<string> RunScriptForManagementAsync(
        this PowerShellHyperVHost host,
        string script,
        CancellationToken cancellationToken) => host.RunScriptForManagementAsyncCore(script, cancellationToken);
}
