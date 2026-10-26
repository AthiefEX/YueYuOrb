param(
    [string]$Version = '1.0.0',
    [switch]$Installer,
    [string]$IsccPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'publish/win-x64'
$testResults = Join-Path $artifactRoot 'test-results'
$zipPath = Join-Path $artifactRoot "YueYuOrb-$Version-win-x64-portable.zip"
$setupPath = Join-Path $artifactRoot "YueYuOrb-$Version-win-x64-setup.exe"
$hashPath = Join-Path $artifactRoot 'SHA256SUMS.txt'

$resolvedRoot = [System.IO.Path]::GetFullPath($root)
$resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactRoot)
if (-not $resolvedArtifacts.StartsWith(
        $resolvedRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact path escaped the repository: $resolvedArtifacts"
}
if (Test-Path -LiteralPath $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $testResults | Out-Null

Push-Location $root
try {
    dotnet restore FloatDesk.sln --locked-mode -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    dotnet build FloatDesk.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet test FloatDesk.sln -c Release --no-build `
        --logger trx --results-directory $testResults
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

    dotnet publish src/FloatDesk.App/FloatDesk.App.csproj -c Release `
        -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:NuGetAudit=false `
        "-p:Version=$Version" `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Compress-Archive -Path (Join-Path $publishDirectory '*') `
        -DestinationPath $zipPath -CompressionLevel Optimal
    $exeHash = Get-FileHash -Algorithm SHA256 `
        -LiteralPath (Join-Path $publishDirectory 'YueYuOrb.exe')
    $zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
    $hashLines = [System.Collections.Generic.List[string]]::new()
    $hashLines.Add(
        "$($exeHash.Hash.ToLowerInvariant())  publish/win-x64/YueYuOrb.exe"
    )
    $hashLines.Add(
        "$($zipHash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"
    )

    if ($Installer) {
        if ([string]::IsNullOrWhiteSpace($IsccPath)) {
            $command = Get-Command 'iscc' -ErrorAction SilentlyContinue
            if ($null -ne $command) {
                $IsccPath = $command.Source
            }
        }
        if ([string]::IsNullOrWhiteSpace($IsccPath)) {
            $candidates = @(
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
                'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
                'C:\Program Files\Inno Setup 6\ISCC.exe'
            )
            $IsccPath = $candidates |
                Where-Object { Test-Path -LiteralPath $_ } |
                Select-Object -First 1
        }
        if ([string]::IsNullOrWhiteSpace($IsccPath)) {
            throw 'Inno Setup 6 ISCC.exe was not found. Install it or pass -IsccPath.'
        }

        & $IsccPath "/DAppVersion=$Version" `
            (Join-Path $root 'installer/FloatDesk.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
        if (-not (Test-Path -LiteralPath $setupPath)) {
            throw "Installer output was not created: $setupPath"
        }

        $setupHash = Get-FileHash -Algorithm SHA256 -LiteralPath $setupPath
        $hashLines.Add(
            "$($setupHash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($setupPath))"
        )
    }

    $hashLines | Set-Content -LiteralPath $hashPath -Encoding ascii

    Write-Host "Published: $publishDirectory"
    Write-Host "Archive:   $zipPath"
    if ($Installer) { Write-Host "Installer: $setupPath" }
    Write-Host "Hashes:    $hashPath"
}
finally {
    Pop-Location
}
