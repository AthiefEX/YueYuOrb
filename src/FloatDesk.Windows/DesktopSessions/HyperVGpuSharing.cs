namespace FloatDesk.Windows.DesktopSessions;

public static class HyperVGpuSharing
{
    /// <summary>
    /// Windows refuses to save the state of a virtual machine that has a GPU partition assigned
    /// (Hyper-V event 19568), so "save state and release memory" cannot succeed while sharing is
    /// on. Reported before the command runs instead of surfacing a localized Hyper-V error.
    /// </summary>
    public static readonly string SaveStateBlockedMessage =
        "该虚拟机已分配显卡分区（GPU 共享），Windows 不允许保存它的状态与内存。请先把关闭行为改为“关机”，或关机一次后在“高级设置”中关闭显卡共享，再使用保存状态。";

    public static async Task<bool> IsEnabledAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(id));
        var output = await new PowerShellHyperVHost().RunScriptForManagementAsyncCore($$"""
            $ErrorActionPreference='Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm=Get-VM -Id ([guid]'{{id:D}}') -ErrorAction Stop
            if (@(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop).Count -gt 0) { 'on' } else { 'off' }
            """, token).ConfigureAwait(false);
        return output == "on";
    }

    public static async Task<string> ApplyAsync(Guid id, bool enabled, CancellationToken token)
    {
        var script = BuildScript(id, enabled);
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "YueYuGpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var resultPath = Path.Combine(directory, "result.json");
        try
        {
            var quotedPath = resultPath.Replace("'", "''", StringComparison.Ordinal);
            var wrapper = "try { $detail = & {" + Environment.NewLine + script + Environment.NewLine +
                "}; @{ok=$true;detail=($detail -join [Environment]::NewLine)} | ConvertTo-Json -Compress | Set-Content -LiteralPath '" +
                quotedPath + "' -Encoding UTF8 } catch { @{ok=$false;detail=$_.Exception.Message} | ConvertTo-Json -Compress | Set-Content -LiteralPath '" +
                quotedPath + "' -Encoding UTF8; exit 1 }";
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                    Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(wrapper))
            }) ?? throw new InvalidOperationException("Cannot start elevated GPU configuration.");
            // Once mounting begins, let the elevated process finish its cleanup even if the UI closes.
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!File.Exists(resultPath)) throw new InvalidOperationException("GPU configuration returned no result.");
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, CancellationToken.None).ConfigureAwait(false));
            var detail = result.RootElement.GetProperty("detail").GetString() ?? string.Empty;
            if (process.ExitCode != 0 || !result.RootElement.GetProperty("ok").GetBoolean())
                throw new InvalidOperationException(detail);
            return detail;
        }
        finally
        {
            if (File.Exists(resultPath)) File.Delete(resultPath);
            Directory.Delete(directory);
        }
    }

    public static string BuildScript(Guid id, bool enabled)
    {
        if (id == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(id));
        return $$"""
            $ErrorActionPreference='Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm=Get-VM -Id ([guid]'{{id:D}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') { throw 'Please shut down guest Windows before changing GPU sharing.' }
            $stateDir=Join-Path $env:LOCALAPPDATA 'YueYuOrb\gpu-sharing'
            $stateFile=Join-Path $stateDir '{{id:D}}.json'
            $adapters=@(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop)
            if ({{(enabled ? "$false" : "$true")}}) {
                if (-not (Test-Path -LiteralPath $stateFile)) { throw 'No YueYu GPU ownership record; refusing to remove external adapters.' }
                $saved=Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
                if ($saved.vmId -ne [string]$vm.Id) { throw 'GPU ownership mismatch.' }
                $adapters | Where-Object { $_.Id -eq $saved.adapterId } | Remove-VMGpuPartitionAdapter -Confirm:$false -ErrorAction Stop
                Set-VM -VM $vm -GuestControlledCacheTypes ([bool]$saved.cache) -LowMemoryMappedIoSpace ([uint64]$saved.low) -HighMemoryMappedIoSpace ([uint64]$saved.high) -Confirm:$false -ErrorAction Stop
                Remove-Item -LiteralPath $stateFile -Force
                'GPU sharing disabled; original memory mapping restored.'
                return
            }
            if ($adapters.Count -gt 0) {
                if (Test-Path -LiteralPath $stateFile) {
                    $saved=Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
                    if ($saved.vmId -eq [string]$vm.Id -and @($adapters | Where-Object { $_.Id -eq $saved.adapterId }).Count -eq 1) { 'GPU partition already configured; guest acceleration requires verification.'; return }
                }
                throw 'Existing GPU partition is not owned by YueYu.'
            }
            if (Test-Path -LiteralPath $stateFile) { throw 'Previous GPU setup requires recovery: disable sharing first.' }
            $query=Get-Command Get-VMHostPartitionableGpu -ErrorAction SilentlyContinue
            if (-not $query) { $query=Get-Command Get-VMPartitionableGpu -ErrorAction Stop }
            $gpus=@(& $query)
            if ($gpus.Count -ne 1) { throw 'Exactly one partitionable GPU is required for automatic selection.' }
            $match=[regex]::Match($gpus[0].Name,'VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}','IgnoreCase')
            if (-not $match.Success) { throw 'Cannot identify GPU hardware.' }
            $drivers=@(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceClass -eq 'DISPLAY' -and $_.DeviceID -like ('*'+$match.Value+'*') })
            if ($drivers.Count -ne 1) { throw 'Cannot uniquely identify GPU driver.' }
            $packages=@(Get-WindowsDriver -Online | Where-Object { $_.Driver -eq $drivers[0].InfName })
            if ($packages.Count -ne 1) { throw 'GPU driver package not found.' }
            $source=Split-Path -Parent $packages[0].OriginalFileName
            if (-not (Test-Path -LiteralPath $source)) { throw 'GPU driver source missing.' }
            $disks=@(Get-VMHardDiskDrive -VM $vm)
            if ($disks.Count -ne 1) { throw 'Exactly one guest disk is required for automatic driver deployment.' }
            $diskPath=$disks[0].Path
            if ((Get-VHD -Path $diskPath).Attached) { throw 'Guest disk is already attached.' }
            $old=@{ vmId=[string]$vm.Id; cache=$vm.GuestControlledCacheTypes; low=[uint64]$vm.LowMemoryMappedIoSpace; high=[uint64]$vm.HighMemoryMappedIoSpace; adapterId='' }
            New-Item -ItemType Directory -Path $stateDir -Force | Out-Null
            $old | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
            $mounted=$false
            $added=$false
            try {
                $vhd=Mount-VHD -Path $diskPath -NoDriveLetter -Passthru -ErrorAction Stop
                $mounted=$true
                $volumes=@($vhd | Get-Disk | Get-Partition | Get-Volume -ErrorAction SilentlyContinue)
                $roots=@($volumes | Where-Object { $_.Path -and (Test-Path -LiteralPath (Join-Path $_.Path 'Windows\System32\config\SYSTEM')) })
                if ($roots.Count -ne 1) { throw 'Cannot identify unlocked Windows partition.' }
                $windows=Join-Path $roots[0].Path 'Windows'
                $repository=Join-Path $windows 'System32\HostDriverStore\FileRepository'
                New-Item -ItemType Directory -Path $repository -Force | Out-Null
                Copy-Item -LiteralPath $source -Destination $repository -Recurse -Force -ErrorAction Stop
                $copied=Join-Path $repository (Split-Path -Leaf $source)
                foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
                    $target=Join-Path $copied $file.FullName.Substring($source.Length+1)
                    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Guest driver verification failed.' }
                }
                if ($match.Value -like 'VEN_10DE*') {
                    foreach ($relative in @('System32\nvapi64.dll','SysWOW64\nvapi.dll')) {
                        $from=Join-Path $env:windir $relative
                        if (Test-Path -LiteralPath $from) { Copy-Item -LiteralPath $from -Destination (Join-Path $windows $relative) -Force -ErrorAction Stop }
                    }
                }
                Dismount-VHD -Path $diskPath -ErrorAction Stop
                $mounted=$false
                Set-VM -VM $vm -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace 1GB -HighMemoryMappedIoSpace 32GB -Confirm:$false -ErrorAction Stop
                Add-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop
                $added=$true
                $current=@(Get-VMGpuPartitionAdapter -VM $vm -ErrorAction Stop)
                if ($current.Count -ne 1) { throw 'GPU partition readback failed.' }
                $old.adapterId=[string]$current[0].Id
                $old | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
                'GPU partition configured; matching driver copied and verified. Start Windows and verify acceleration in Device Manager.'
            } catch {
                $failure=$_
                if ($added) { Get-VMGpuPartitionAdapter -VM $vm | Remove-VMGpuPartitionAdapter -Confirm:$false -ErrorAction Stop }
                Set-VM -VM $vm -GuestControlledCacheTypes ([bool]$old.cache) -LowMemoryMappedIoSpace $old.low -HighMemoryMappedIoSpace $old.high -Confirm:$false -ErrorAction Stop
                Remove-Item -LiteralPath $stateFile -Force
                throw $failure
            } finally {
                if ($mounted) { Dismount-VHD -Path $diskPath -ErrorAction Stop }
            }
            """;
    }
}
