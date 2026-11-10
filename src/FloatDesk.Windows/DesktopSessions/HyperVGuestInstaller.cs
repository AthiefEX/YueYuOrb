namespace FloatDesk.Windows.DesktopSessions;

public static class HyperVGuestInstaller
{
    public static string BuildInstallScript() => """
        $ErrorActionPreference = 'Stop'
        Write-Host '[1/4] Checking installation and payload...'
        $mutex = New-Object Threading.Mutex($false, 'Local\YueYuGuestInstaller')
        if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); throw 'Another guest installation is running. Please wait for it to finish.' }
        try {
        $bootstrap = Join-Path $PSScriptRoot 'bootstrap.json'
        $payload = Join-Path $PSScriptRoot 'agent'
        $target = Join-Path $env:LOCALAPPDATA 'YueYuOrb\guest\agent'
        $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
        if ($manifest.Count -eq 0) { throw 'Empty payload manifest' }
        foreach ($entry in $manifest) {
            $source = [IO.Path]::GetFullPath((Join-Path $payload $entry.Path))
            if (-not $source.StartsWith([IO.Path]::GetFullPath($payload) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid payload path' }
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Agent payload hash mismatch' }
        }
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Write-Host '[2/4] Installing verified Agent files...'
        foreach ($entry in $manifest) {
            $destination = Join-Path $target $entry.Path
            if ((Test-Path -LiteralPath $destination) -and
                (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $entry.Hash) { continue }
            $agentExe = Join-Path $target 'YueYuOrb.SessionAgent.exe'
            Get-Process -Name 'YueYuOrb.SessionAgent' -ErrorAction SilentlyContinue | Where-Object {
                $_.Path -and $_.Path.Equals($agentExe, [StringComparison]::OrdinalIgnoreCase)
            } | ForEach-Object { $_ | Stop-Process -Force; $_.WaitForExit() }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $payload $entry.Path) -Destination $destination -Force
            if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.Hash) { throw 'Installed Agent hash mismatch' }
        }
        $exe = Join-Path $target 'YueYuOrb.SessionAgent.exe'
        if (-not (Test-Path -LiteralPath $bootstrap)) {
            $saved = Join-Path $env:LOCALAPPDATA 'YueYuOrb\guest\pairing.dat'
            if (-not (Test-Path -LiteralPath $saved)) { throw 'Pairing configuration is missing. Export a new initialization package.' }
            $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
            New-Item -Path $run -Force | Out-Null
            Set-ItemProperty -Path $run -Name 'YueYuGuestAgent' -Value ('"' + $exe + '" --hyperv-guest')
            $running = Get-Process -Name 'YueYuOrb.SessionAgent' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase) }
            if (-not $running) { Start-Process -FilePath $exe -ArgumentList '--hyperv-guest' -WindowStyle Hidden }
            Write-Host 'Agent already configured. Return to YueYu and test pairing.'
            return
        }
        Write-Host '[3/4] Saving protected pairing configuration...'
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $exe
        $info.Arguments = '--hyperv-configure'
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardInput = $true
        $process = [Diagnostics.Process]::Start($info)
        try {
            $process.StandardInput.WriteLine((Get-Content -LiteralPath $bootstrap -Raw))
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Guest configuration timed out' }
            if ($process.ExitCode -ne 0) { throw 'Guest configuration failed; verify VM identity and interactive user' }
        } finally { $process.Dispose() }
        $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        New-Item -Path $run -Force | Out-Null
        Set-ItemProperty -Path $run -Name 'YueYuGuestAgent' -Value ('"' + $exe + '" --hyperv-guest')
        Remove-Item -LiteralPath $bootstrap -Force
        Write-Host '[4/4] Starting guest Agent...'
        $running = Get-Process -Name 'YueYuOrb.SessionAgent' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.Equals($exe, [StringComparison]::OrdinalIgnoreCase) }
        if (-not $running) { Start-Process -FilePath $exe -ArgumentList '--hyperv-guest' -WindowStyle Hidden }
        Write-Host 'Guest Agent configured. Return to YueYu and test pairing.'
        } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
        """;
}
