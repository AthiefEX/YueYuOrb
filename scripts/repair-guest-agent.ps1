$ErrorActionPreference = 'Stop'
$guestRoot = Join-Path $env:LOCALAPPDATA 'YueYuOrb\guest'
$agentExe = Join-Path $guestRoot 'agent\YueYuOrb.SessionAgent.exe'
$pairing = Join-Path $guestRoot 'pairing.dat'
try {
    if (-not (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters')) {
        throw 'NotAGuest: Run this inside the Hyper-V guest, not on the host.'
    }
    if (-not (Test-Path -LiteralPath $agentExe)) {
        throw 'AgentMissing: Run this inside the guest Windows account used during Agent installation.'
    }
    if (-not (Test-Path -LiteralPath $pairing)) {
        throw 'PairingMissing: This Windows account has no pairing configuration. Use the original guest account.'
    }
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -Path $runKey -Name 'YueYuGuestAgent' -Value ('"' + $agentExe + '" --hyperv-guest')
    $currentSession = (Get-Process -Id $PID).SessionId
    $existing = @(Get-Process -Name 'YueYuOrb.SessionAgent' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq $currentSession -and $_.Path -and $_.Path.Equals($agentExe, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($agentProcess in $existing) {
        $agentProcess | Stop-Process -Force
        $agentProcess.WaitForExit()
    }
    $agentProcess = Start-Process -FilePath $agentExe -ArgumentList '--hyperv-guest' -WindowStyle Hidden -PassThru
    if ($agentProcess.WaitForExit(3000)) {
        throw ('AgentExited: exit code ' + $agentProcess.ExitCode + '. Pairing was preserved.')
    }
    Write-Host 'AgentRunning: login startup repaired. Return to YueYu and test Agent pairing.'
    Write-Host 'The process staying alive does not yet prove the authenticated host connection works.'
    exit 0
} catch {
    Write-Host ('RepairFailed: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
