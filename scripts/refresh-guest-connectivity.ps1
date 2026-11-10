$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters')) {
    throw 'Run this inside the Hyper-V guest.'
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'"'
    $elevated = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Verb RunAs -ArgumentList $arguments -PassThru -Wait
    exit $elevated.ExitCode
}
$result = @{ internet=$false; networkRefresh=$false; taskManagerRestart=$false }
try {
    Clear-DnsClientCache
    $service = Get-Service NlaSvc
    $dependents = @($service.DependentServices | Where-Object Status -eq Running | Select-Object -ExpandProperty Name)
    try {
        Restart-Service NlaSvc -Force -ErrorAction Stop
        (Get-Service NlaSvc).WaitForStatus('Running',[TimeSpan]::FromSeconds(15))
        $result.networkRefresh=$true
    } finally {
        foreach($name in $dependents) {
            if ((Get-Service $name).Status -ne 'Running') { Start-Service $name -ErrorAction Continue }
        }
    }
    $session=(Get-Process -Id $PID).SessionId
    $managers=@(Get-Process Taskmgr -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session)
    if ($managers.Count -gt 0) {
        $managers | Stop-Process -Force
        Start-Process -FilePath "$env:SystemRoot\System32\Taskmgr.exe"
        $result.taskManagerRestart=$true
    }
    for($i=0;$i -lt 30;$i++) {
        if (@(Get-NetConnectionProfile | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }).Count -gt 0) {
            $result.internet=$true
            break
        }
        Start-Sleep -Seconds 1
    }
} catch {
    $result.errorType=$_.Exception.GetType().Name
} finally {
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'result.json') -Encoding UTF8
}
Write-Host ('Network detection refreshed: '+$result.networkRefresh)
Write-Host ('Windows reports Internet: '+$result.internet)
Write-Host 'GPU hardware acceleration and the Task Manager GPU page are separate checks.'
