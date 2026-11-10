using System.Diagnostics;
using System.Text.Json;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.DesktopSessions;

public enum HyperVVirtualMachineState { Unknown, Off, Running, Paused, Saved }

public sealed record HyperVVirtualMachine(
    Guid Id,
    string Name,
    HyperVVirtualMachineState State,
    int Generation,
    long MemoryStartupMiB,
    int ProcessorCount,
    bool OwnedByProduct = false)
{
    public int CpuUsagePercent { get; init; }
    public long MemoryAssignedMiB { get; init; }
    public string DisplayStatus => $"{Name} · {State} · {ProcessorCount} CPU · 使用率 {CpuUsagePercent}% · 已分配 {MemoryAssignedMiB} MiB";
}

public sealed record HyperVVmCreateRequest(
    Guid EnvironmentId,
    string Name,
    string StorageDirectory,
    string IsoPath,
    int ProcessorCount,
    long MemoryStartupMiB,
    long DiskSizeGiB,
    string SwitchName,
    bool SecureBoot)
{
    public string? TemplatePath { get; init; }
}

public sealed record HyperVVmDeleteRequest(Guid VmId, bool DeleteConfiguration, bool DeleteDisk);

public sealed class HyperVVirtualMachineManager
{
    private readonly PowerShellHyperVHost _host;

    public HyperVVirtualMachineManager(PowerShellHyperVHost? host = null) => _host = host ?? new PowerShellHyperVHost();

    public async Task<IReadOnlyList<HyperVVirtualMachine>> ListAsync(CancellationToken cancellationToken)
    {
        var script = BuildListScript();
        var json = await _host.RunScriptForManagementAsyncCore(script, cancellationToken).ConfigureAwait(false);
        return ParseVmList(json);
    }

    public static string BuildListScript() => """
        $ErrorActionPreference = 'Stop'
        Import-Module Hyper-V -ErrorAction Stop
        $managedRoot = Join-Path $env:LOCALAPPDATA 'YueYuOrb\HyperV'
        $vms = @(Get-VM | ForEach-Object { [pscustomobject]@{ id=$_.Id; name=$_.Name; state=[string]$_.State; generation=$_.Generation; memoryStartupMiB=[int64]($_.MemoryStartup/1MB); processorCount=$_.ProcessorCount; cpuUsage=[int]$_.CPUUsage; memoryAssignedMiB=[int64]($_.MemoryAssigned/1MB); ownedByProduct=(([string]$_.Notes -like '*YueYuOrb:managed*') -or (([string]$_.Name -like '月语分身-*') -and ([string]$_.Path -like ($managedRoot + '*')))) } })
        if ($vms.Count -eq 0) { '[]' } else { $vms | ConvertTo-Json -Compress }
        """;

    public static ProcessStartInfo BuildVmConnectCommand(string vmName, bool embedded = false)
    {
        if (string.IsNullOrWhiteSpace(vmName))
            throw new ArgumentException("A VM name is required.", nameof(vmName));

        var command = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "vmconnect.exe"),
            UseShellExecute = !embedded,
            CreateNoWindow = embedded,
            WindowStyle = embedded ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
        };
        command.ArgumentList.Add("localhost");
        command.ArgumentList.Add(vmName);
        return command;
    }

    public async Task<HyperVVirtualMachine> CreateAsync(HyperVVmCreateRequest request, CancellationToken cancellationToken)
    {
        ValidateCreate(request);
        var json = await _host.RunScriptForManagementAsyncCore(BuildCreateScript(request), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var createdId = document.RootElement.GetProperty("id").GetGuid();
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).First(vm => vm.Id == createdId);
    }

    public async Task StartAsync(Guid vmId, CancellationToken cancellationToken) => await _host.StartVmAsync(vmId, cancellationToken).ConfigureAwait(false);
    public async Task StopAsync(Guid vmId, CancellationToken cancellationToken) => await _host.StopVmAsync(vmId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Applies an environment close action (save state or turn off) and verifies the resulting
    /// state. Saving is refused up front for a machine with a GPU partition assigned, because the
    /// host rejects that combination every time.
    /// </summary>
    public async Task ApplyCloseActionAsync(Guid vmId, HyperVConsoleCloseAction action, CancellationToken cancellationToken)
    {
        if (action == HyperVConsoleCloseAction.SaveState &&
            await HyperVGpuSharing.IsEnabledAsync(vmId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(HyperVGpuSharing.SaveStateBlockedMessage);
        }
        await _host.RunScriptForManagementAsyncCore(
            HyperVConsoleSettings.BuildCloseScript(vmId, action), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(Guid vmId, bool deleteDisk, bool ownedByProduct, CancellationToken cancellationToken)
    {
        ValidateDelete(new HyperVVmDeleteRequest(vmId, true, deleteDisk), ownedByProduct);
        return _host.RunScriptForManagementAsyncCore(BuildDeleteScript(vmId, deleteDisk), cancellationToken);
    }

    public static string BuildDeleteScript(Guid vmId, bool deleteDisk)
    {
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') { throw 'HyperVVmMustBeOff' }
            $paths = @($vm.HardDrives | ForEach-Object { $_.Path })
            Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop
            if ({{(deleteDisk ? "$true" : "$false")}}) { $paths | Where-Object { $_ -and (Test-Path $_) } | Remove-Item -Force }
            'ok'
            """;
    }

    public static void ValidateCreate(HyperVVmCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentId == Guid.Empty) throw new InvalidOperationException("HyperVEnvironmentRequired");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) throw new InvalidOperationException("HyperVVmNameInvalid");
        ValidateCreateForScript(request);
        var sourcePath = request.TemplatePath ?? request.IsoPath;
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("HyperVInstallMediaMissing", sourcePath);
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
        var source = request.TemplatePath?.Replace("'", "''", StringComparison.Ordinal);
        var createDisk = source is null
            ? $"New-VHD -Path $vhd -SizeBytes {request.DiskSizeGiB}GB -Dynamic -ErrorAction Stop | Out-Null"
            : $$"""
              $source = '{{source}}'
              $info = Get-VHD -Path $source -ErrorAction Stop
              if ($info.Attached -or $info.ParentPath) { throw 'TemplateInUseOrDifferencingDisk' }
              if ($info.Size -gt {{request.DiskSizeGiB}}GB) { throw 'TemplateDiskCannotShrink' }
              Copy-Item -LiteralPath $source -Destination $vhd -ErrorAction Stop
              """;
        var bootMedia = source is null
            ? "Add-VMDvdDrive -VMName $name -Path $iso -ErrorAction Stop\nSet-VMFirmware -VMName $name -FirstBootDevice (Get-VMDvdDrive -VMName $name) -ErrorAction Stop"
            : $"if ($info.Size -lt {request.DiskSizeGiB}GB) {{ Resize-VHD -Path $vhd -SizeBytes {request.DiskSizeGiB}GB -ErrorAction Stop }}\nSet-VMFirmware -VMName $name -FirstBootDevice (Get-VMHardDiskDrive -VMName $name | Select-Object -First 1) -ErrorAction Stop";
        return $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $name = '{{escapedName}}'
            $root = '{{storage}}'
            $vhd = Join-Path $root ($name + '.vhdx')
            $iso = '{{iso}}'
            $vm = $null
            $createdDisk = $false
            if (Test-Path -LiteralPath $vhd) { throw 'HyperVDiskAlreadyExists' }
            Get-VMSwitch -Name '{{switchName}}' -ErrorAction Stop | Out-Null
            try {
                New-Item -ItemType Directory -Force -Path $root | Out-Null
                if (Get-VM -Name $name -ErrorAction SilentlyContinue) { throw 'HyperVVmAlreadyExists' }
                {{createDisk}}
                $createdDisk = $true
                $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes {{request.MemoryStartupMiB}}MB -VHDPath $vhd -Path $root -SwitchName '{{switchName}}' -ErrorAction Stop
                Set-VM -VM $vm -ProcessorCount {{request.ProcessorCount}} -AutomaticStopAction ShutDown -Notes 'YueYuOrb:managed' -ErrorAction Stop
                {{bootMedia}}
                if ({{secureBoot}}) { Set-VMFirmware -VMName $name -EnableSecureBoot On -ErrorAction Stop }
                [pscustomobject]@{ id = $vm.Id; name = $vm.Name; vhd = $vhd } | ConvertTo-Json -Compress
            }
            catch {
                if ($null -ne $vm) { Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop }
                if ($createdDisk -and (Test-Path -LiteralPath $vhd)) { Remove-Item -LiteralPath $vhd -Force -ErrorAction Stop }
                throw
            }
            """;
    }

    public static string BuildCreateScriptForTest(HyperVVmCreateRequest request) =>
        BuildCreateScript(request);

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
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToArray()
            : new[] { root };
        foreach (var item in items)
        {
            if (!Guid.TryParse(item.GetProperty("id").GetString(), out var id)) continue;
            var owned = item.TryGetProperty("ownedByProduct", out var ownedProperty) && ownedProperty.ValueKind == JsonValueKind.True;
            list.Add(new(id, item.GetProperty("name").GetString() ?? string.Empty, ParseState(item.GetProperty("state").GetString()), item.GetProperty("generation").GetInt32(), item.GetProperty("memoryStartupMiB").GetInt64(), item.GetProperty("processorCount").GetInt32(), owned)
            {
                CpuUsagePercent = item.TryGetProperty("cpuUsage", out var cpu) ? cpu.GetInt32() : 0,
                MemoryAssignedMiB = item.TryGetProperty("memoryAssignedMiB", out var memory) ? memory.GetInt64() : 0
            });
        }
        return list;
    }

    private static HyperVVirtualMachineState ParseState(string? value) => value?.ToUpperInvariant() switch
    {
        "OFF" or "3" => HyperVVirtualMachineState.Off,
        "RUNNING" or "2" => HyperVVirtualMachineState.Running,
        "PAUSED" or "9" => HyperVVirtualMachineState.Paused,
        "SAVED" or "6" => HyperVVirtualMachineState.Saved,
        _ => HyperVVirtualMachineState.Unknown
    };

    private static void ValidateCreateForScript(HyperVVmCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.EnvironmentId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.ProcessorCount < 2 || request.MemoryStartupMiB < 2048 || request.DiskSizeGiB < 32) throw new InvalidOperationException("HyperVCreateRequestInvalid");
        if (!Path.IsPathFullyQualified(request.StorageDirectory) || !Path.IsPathFullyQualified(request.TemplatePath ?? request.IsoPath)) throw new InvalidOperationException("HyperVPathInvalid");
    }
}
