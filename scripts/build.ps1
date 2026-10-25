param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Locked
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ($Locked) {
        dotnet restore FloatDesk.sln --locked-mode
    }
    else {
        dotnet restore FloatDesk.sln
    }
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

    dotnet build FloatDesk.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
finally {
    Pop-Location
}
