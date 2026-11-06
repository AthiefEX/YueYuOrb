param(
    [string]$Executable = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Executable)) {
    $Executable = Join-Path $root 'src/FloatDesk.App/bin/Debug/net10.0-windows/YueYuOrb.exe'
}
$Executable = [System.IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable)) {
    throw "YueYuOrb executable was not found: $Executable"
}
if (Get-Process YueYuOrb -ErrorAction SilentlyContinue) {
    throw 'Stop any running YueYuOrb instance before running this smoke test.'
}
$settingsWindowTitle = [string]::Concat(
    [char]0x6708, [char]0x8BED, [char]0x60AC,
    [char]0x6D6E, [char]0x7403, [char]0x8BBE, [char]0x7F6E)

$logPath = Join-Path $env:LOCALAPPDATA (
    'FloatDesk/logs/app-' + [DateTime]::UtcNow.ToString('yyyyMMdd') + '.log')
$beforeLines = if (Test-Path -LiteralPath $logPath) {
    (Get-Content -LiteralPath $logPath).Count
}
else {
    0
}

$first = $null
$second = $null
try {
    $first = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds(15)
    $startupComplete = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (-not (Test-Path -LiteralPath $logPath)) { continue }
        $newLines = Get-Content -LiteralPath $logPath | Select-Object -Skip $beforeLines
        if ($newLines | Select-String -SimpleMatch 'startup.complete') {
            $startupComplete = $true
            break
        }
    }
    if (-not $startupComplete) {
        throw "startup.complete was not observed within 15 seconds in $logPath."
    }
    if ($first.HasExited) { throw "Primary process exited unexpectedly: $($first.ExitCode)" }

    $second = Start-Process -FilePath $Executable -PassThru -WindowStyle Hidden
    if (-not $second.WaitForExit(5000)) {
        throw 'Second instance did not exit within 5 seconds.'
    }
    if ($second.ExitCode -ne 0) {
        throw "Second instance exited with code $($second.ExitCode)."
    }

    $settingsDeadline = (Get-Date).AddSeconds(8)
    $settingsOpened = $false
    while ((Get-Date) -lt $settingsDeadline) {
        $current = Get-Process -Id $first.Id -ErrorAction SilentlyContinue
        if ($null -eq $current) {
            throw 'Primary process exited after the second instance requested settings.'
        }
        $current.Refresh()
        if ($current.MainWindowTitle -eq $settingsWindowTitle) {
            $settingsOpened = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $settingsOpened) {
        throw 'The second instance exited, but the settings window did not open.'
    }

    Write-Host 'Smoke PASS: startup.complete observed; settings opened; primary stayed alive; second instance exited with code 0.'
}
finally {
    foreach ($process in @($second, $first)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force
        }
    }
}
