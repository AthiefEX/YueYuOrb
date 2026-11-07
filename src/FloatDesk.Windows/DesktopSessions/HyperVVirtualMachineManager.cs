using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public enum HyperVVirtualMachineState { Unknown, Off, Running, Paused, Saved }

public sealed record HyperVVirtualMachine(
    Guid Id,
    string Name,
    HyperVVirtualMachineState State,
    int Generation,
    long MemoryStartupMiB,
    int ProcessorCount);

public sealed record HyperVVmCreateRequest(
    Guid EnvironmentId,
    string Name,
    string StorageDirectory,
    string IsoPath,
    int ProcessorCount,
    long MemoryStartupMiB,
    long DiskSizeGiB,
    string SwitchName,
    bool SecureBoot);

public sealed record HyperVVmDeleteRequest(Guid VmId, bool DeleteConfiguration, bool DeleteDisk);

public sealed class HyperVVirtualMachineManager
{
    private readonly PowerShellHyperVHost _host;

    public HyperVVirtualMachineManager(PowerShellHyperVHost? host = null) => _host = host ?? new PowerShellHyperVHost();

    public async Task<IReadOnlyList<HyperVVirtualMachine>> ListAsync(CancellationToken cancellationToken)
    {
        var script = "Import-Module Hyper-V -ErrorAction Stop; Get-VM | ForEach-Object { [pscustomobject]@{ id=$_.Id; name=$_.Name; state=[string]$_.State; generation=$_.Generation; memoryStartupMiB=[int64]($_.MemoryStartup/1MB); processorCount=$_.ProcessorCount } } | ConvertTo-Json -Compress";
        var json = await _host.RunScriptForManagementAsyncCore(script, cancellationToken).ConfigureAwait(false);
        return ParseVmList(json);
    }

    public async Task<HyperVVirtualMachine> CreateAsync(HyperVVmCreateRequest request, CancellationToken cancellationToken)
    {
        ValidateCreate(request);
        var json = await _host.RunScriptForManagementAsyncCore(BuildCreateScript(request), cancellationToken).ConfigureAwait(false);
        var created = JsonDocument.Parse(json).RootElement;
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).First(vm => vm.Id == created.GetProperty("id").GetGuid());
    }

    public async Task StartAsync(Guid vmId, CancellationToken cancellationToken) => await _host.StartVmAsync(vmId, cancellationToken).ConfigureAwait(false);
    public async Task StopAsync(Guid vmId, CancellationToken cancellationToken) => await _host.StopVmAsync(vmId, cancellationToken).ConfigureAwait(false);

    public Task DeleteAsync(Guid vmId, bool deleteDisk, bool ownedByProduct, CancellationToken cancellationToken)
    {
        ValidateDelete(new HyperVVmDeleteRequest(vmId, true, deleteDisk), ownedByProduct);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') { throw 'HyperVVmMustBeOff' }
            $paths = @($vm.HardDrives | ForEach-Object { $_.Path })
            Remove-VM -VM $vm -Force -ErrorAction Stop
            if ({{(deleteDisk ? "$true" : "$false")}}) { $paths | Where-Object { $_ -and (Test-Path $_) } | Remove-Item -Force }
            """;
        return _host.RunScriptForManagementAsyncCore(script, cancellationToken);
    }

    public static void ValidateCreate(HyperVVmCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentId == Guid.Empty) throw new InvalidOperationException("HyperVEnvironmentRequired");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) throw new InvalidOperationException("HyperVVmNameInvalid");
        if (!Path.IsPathFullyQualified(request.StorageDirectory) || !Path.IsPathFullyQualified(request.IsoPath)) throw new InvalidOperationException("HyperVPathInvalid");
        if (!File.Exists(request.IsoPath)) throw new FileNotFoundException("HyperVInstallMediaMissing", request.IsoPath);
        if (request.ProcessorCount is < 2 or > 128 || request.MemoryStartupMiB < 2048 || request.DiskSizeGiB < 32) throw new InvalidOperationException("HyperVResourcesInvalid");
        if (string.IsNullOrWhiteSpace(request.SwitchName)) throw new InvalidOperationException("HyperVSwitchRequired");
    }

    public static string BuildCreateScript(HyperVVmCreateRequest request)
    {
        ValidateCreateForScript(request);
        var escapedName = request.Name.Replace("'", "''", StringComparison.Ordinal);
        var storage = request.StorageDirectory.Replace("'", "''", StringComparison.Ordinal).TrimEnd('\\');
        var iso = request.IsoPath.Replace("'", "''", StringComparison.Ordinal);
        var switchName = request.SwitchName.Replace("'", "''", StringComparison.Ordinal);
        var secureBoot = request.SecureBoot ? "$true" : "$false";
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $name = '{{escapedName}}'
            $root = '{{storage}}'
            $vhd = Join-Path $root ($name + '.vhdx')
            $iso = '{{iso}}'
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw 'HyperVVmAlreadyExists' }
            New-VHD -Path $vhd -SizeBytes {{request.DiskSizeGiB}}GB -Dynamic -ErrorAction Stop | Out-Null
            $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes {{request.MemoryStartupMiB}}MB -VHDPath $vhd -Path $root -SwitchName '{{switchName}}' -ErrorAction Stop
            Set-VM -VM $vm -ProcessorCount {{request.ProcessorCount}} -AutomaticStopAction ShutDown -ErrorAction Stop
            Add-VMDvdDrive -VMName $name -Path $iso -ErrorAction Stop
            if ({{secureBoot}}) { Set-VMFirmware -VMName $name -EnableSecureBoot On -ErrorAction Stop }
            [pscustomobject]@{ id = $vm.Id; name = $vm.Name; vhd = $vhd } | ConvertTo-Json -Compress
            """;
    }

    public static void ValidateDelete(HyperVVmDeleteRequest request, bool ownedByProduct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.VmId == Guid.Empty) throw new InvalidOperationException("HyperVVmIdentityRequired");
        if (!ownedByProduct) throw new InvalidOperationException("HyperVVmOwnershipRequired");
    }

    public static IReadOnlyList<HyperVVirtualMachine> ParseVmList(string json)
    {
        using var document = JsonDocument.Parse(json);
        var list = new List<HyperVVirtualMachine>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!Guid.TryParse(item.GetProperty("id").GetString(), out var id)) continue;
            list.Add(new(id, item.GetProperty("name").GetString() ?? string.Empty, ParseState(item.GetProperty("state").GetString()), item.GetProperty("generation").GetInt32(), item.GetProperty("memoryStartupMiB").GetInt64(), item.GetProperty("processorCount").GetInt32()));
        }
        return list;
    }

    private static HyperVVirtualMachineState ParseState(string? value) => value?.ToUpperInvariant() switch
    {
        "OFF" => HyperVVirtualMachineState.Off,
        "RUNNING" => HyperVVirtualMachineState.Running,
        "PAUSED" => HyperVVirtualMachineState.Paused,
        "SAVED" => HyperVVirtualMachineState.Saved,
        _ => HyperVVirtualMachineState.Unknown
    };

    private static void ValidateCreateForScript(HyperVVmCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.ProcessorCount < 2 || request.MemoryStartupMiB < 2048 || request.DiskSizeGiB < 32) throw new InvalidOperationException("HyperVCreateRequestInvalid");
        if (!Path.IsPathFullyQualified(request.StorageDirectory) || !Path.IsPathFullyQualified(request.IsoPath)) throw new InvalidOperationException("HyperVPathInvalid");
    }
}
