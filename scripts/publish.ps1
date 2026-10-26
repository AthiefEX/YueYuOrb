param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $root 'artifacts'
$publishDirectory = Join-Path $artifactRoot 'publish/win-x64'
$testResults = Join-Path $artifactRoot 'test-results'
$zipPath = Join-Path $artifactRoot "YueYuOrb-$Version-win-x64-portable.zip"
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
    dotnet restore FloatDesk.sln --locked-mode
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
        "-p:Version=$Version" `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Compress-Archive -Path (Join-Path $publishDirectory '*') `
        -DestinationPath $zipPath -CompressionLevel Optimal
    $exeHash = Get-FileHash -Algorithm SHA256 `
        -LiteralPath (Join-Path $publishDirectory 'YueYuOrb.exe')
    $zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
    @(
        "$($exeHash.Hash.ToLowerInvariant())  publish/win-x64/YueYuOrb.exe"
        "$($zipHash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"
    ) | Set-Content -LiteralPath $hashPath -Encoding ascii

    Write-Host "Published: $publishDirectory"
    Write-Host "Archive:   $zipPath"
    Write-Host "Hashes:    $hashPath"
}
finally {
    Pop-Location
}
