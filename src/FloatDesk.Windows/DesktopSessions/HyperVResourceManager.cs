using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record HyperVSwitch(string Name, string Type);
public sealed record HyperVAppliedResources(int CpuCount, long MemoryStartupMiB, bool FixedMemoryForGpu = false);

public sealed class HyperVResourceManager
{
    private readonly PowerShellHyperVHost _host = new();

    public Task ConnectNetworkAsync(Guid vmId, string switchName, CancellationToken cancellationToken) =>
        _host.RunScriptForManagementAsyncCore(BuildNetworkScript(vmId, switchName), cancellationToken);

    public static string BuildNetworkScript(Guid vmId, string switchName)
    {
        if (vmId == Guid.Empty) throw new ArgumentException("A VM identity is required.", nameof(vmId));
        ArgumentException.ThrowIfNullOrWhiteSpace(switchName);
        var escaped = switchName.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            $switch = Get-VMSwitch -Name '{{escaped}}' -ErrorAction Stop
            $adapters = @(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)
            if ($adapters.Count -ne 1) { throw 'AmbiguousVmNetworkAdapters' }
            $adapter = $adapters[0]
            $oldSwitch = $adapter.SwitchName
            try {
                Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -SwitchName $switch.Name -Confirm:$false -ErrorAction Stop
                $current = Get-VMNetworkAdapter -VM $vm -ErrorAction Stop
                if ($current.SwitchName -ne $switch.Name) { throw 'NetworkReadbackMismatch' }
                [pscustomobject]@{ connected=$true; switch=$current.SwitchName } | ConvertTo-Json -Compress
            } catch {
                $failure = $_
                if ($oldSwitch) { Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -SwitchName $oldSwitch -Confirm:$false -ErrorAction Stop }
                else { Disconnect-VMNetworkAdapter -VMNetworkAdapter $adapter -Confirm:$false -ErrorAction Stop }
                throw $failure
            }
            """;
    }

    public async Task<IReadOnlyList<HyperVSwitch>> ListSwitchesAsync(CancellationToken cancellationToken)
    {
        var output = await _host.RunScriptForManagementAsyncCore("""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $switches = @(Get-VMSwitch | ForEach-Object { [pscustomobject]@{ name=$_.Name; type=[string]$_.SwitchType } })
            ConvertTo-Json -InputObject $switches -Compress
            """, cancellationToken).ConfigureAwait(false);
        return ParseSwitches(output);
    }

    public static IReadOnlyList<HyperVSwitch> ParseSwitches(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var values = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root];
        return values.Select(value => new HyperVSwitch(
            value.GetProperty("name").GetString() ?? string.Empty,
            value.GetProperty("type").GetString() ?? string.Empty)).ToArray();
    }

    public async Task<HyperVAppliedResources> ApplyAsync(Guid vmId, int cpu, long memory, CancellationToken cancellationToken)
    {
        var output = await _host.RunScriptForManagementAsyncCore(BuildApplyScript(vmId, cpu, memory), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        return new(document.RootElement.GetProperty("cpu").GetInt32(), document.RootElement.GetProperty("memory").GetInt64(),
            document.RootElement.GetProperty("fixedMemoryForGpu").GetBoolean());
    }

    public static string BuildApplyScript(Guid vmId, int cpu, long memory)
    {
        if (vmId == Guid.Empty) throw new ArgumentException("A VM identity is required.", nameof(vmId));
        if (cpu is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(cpu));
        if (memory is < 2048 or > 1048576) throw new ArgumentOutOfRangeException(nameof(memory));
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') { throw 'VmMustBeOffForResourceChange' }
            $oldCpu = $vm.ProcessorCount
            $oldMemory = $vm.MemoryStartup
            $hasGpu = @(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop).Count -gt 0
            $memoryMode = @{}
            # GPU partitioning cannot use an unequal dynamic-memory range.
            # Apply CPU, startup memory and fixed mode in one Hyper-V operation.
            if ($hasGpu) { $memoryMode.StaticMemory = $true }
            try {
                Set-VM -VM $vm -ProcessorCount {{cpu}} -MemoryStartupBytes {{memory}}MB @memoryMode -Confirm:$false -ErrorAction Stop
                $current = Get-VM -Id $vm.Id -ErrorAction Stop
                $currentMemory = Get-VMMemory -VM $current -ErrorAction Stop
                if ($current.ProcessorCount -ne {{cpu}} -or $current.MemoryStartup -ne {{memory}}MB) { throw 'ResourceReadbackMismatch' }
                if ($hasGpu -and $currentMemory.DynamicMemoryEnabled) { throw 'GpuMemoryReadbackMismatch' }
                [pscustomobject]@{ cpu=$current.ProcessorCount; memory=[int64]($current.MemoryStartup/1MB); fixedMemoryForGpu=$hasGpu } | ConvertTo-Json -Compress
            } catch {
                $failure = $_
                try {
                    # Restore capacity while keeping GPU-compatible fixed mode.
                    # Re-enabling a preexisting incompatible dynamic range would
                    # fail again and hide the original error.
                    Set-VM -VM $vm -ProcessorCount $oldCpu -MemoryStartupBytes $oldMemory @memoryMode -Confirm:$false -ErrorAction Stop
                    $restored = Get-VM -Id $vm.Id -ErrorAction Stop
                    if ($restored.ProcessorCount -ne $oldCpu -or $restored.MemoryStartup -ne $oldMemory) { throw 'ResourceRollbackReadbackMismatch' }
                } catch { throw ('ResourceRollbackFailed: '+$_.Exception.Message+'; Original: '+$failure.Exception.Message) }
                if ($hasGpu) { throw ('修改失败；已恢复原 CPU 和内存容量，并保持显卡共享所需的固定内存。原因：'+$failure.Exception.Message) }
                throw $failure
            }
            """;
    }
}
