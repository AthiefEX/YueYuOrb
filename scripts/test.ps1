param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'

Push-Location $root
try {
    dotnet restore FloatDesk.sln --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    dotnet build FloatDesk.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    New-Item -ItemType Directory -Force -Path $results | Out-Null
    dotnet test FloatDesk.sln -c $Configuration --no-build `
        --logger trx --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
