param(
    [string]$Version = '1.4.0',
    [switch]$Installer,
    [string]$IsccPath = '',
    [string]$ArtifactRoot = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    Join-Path $root 'artifacts'
} elseif ([System.IO.Path]::IsPathRooted($ArtifactRoot)) {
    $ArtifactRoot
} else {
    Join-Path $root $ArtifactRoot
}
$publishDirectory = Join-Path $artifactRoot 'publish/win-x64'
$sessionAgentDirectory = Join-Path $publishDirectory 'desktop-sessions/agent'
$sessionClientDirectory = Join-Path $publishDirectory 'desktop-sessions/client'
$sessionBrokerDirectory = Join-Path $publishDirectory 'desktop-sessions/broker'
$testResults = Join-Path $artifactRoot 'test-results'
$zipPath = Join-Path $artifactRoot "YueYuOrb-$Version-win-x64-portable.zip"
$sessionAgentZipPath = Join-Path $artifactRoot "YueYuOrb-$Version-session-agent-win-x64.zip"
$setupPath = Join-Path $artifactRoot "YueYuOrb-$Version-win-x64-setup.exe"
$hashPath = Join-Path $artifactRoot 'SHA256SUMS.txt'
$buildArguments = @(
    '-p:UseArtifactsOutput=true',
    "-p:ArtifactsPath=$(Join-Path $artifactRoot 'build')"
)

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
    dotnet restore FloatDesk.sln --locked-mode -p:NuGetAudit=false @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }

    dotnet build FloatDesk.sln -c Release --no-restore @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet test FloatDesk.sln -c Release --no-build `
        --logger trx --results-directory $testResults @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed.' }

    dotnet publish src/FloatDesk.App/FloatDesk.App.csproj -c Release `
        -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:NuGetAudit=false `
        "-p:Version=$Version" `
        -o $publishDirectory @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    dotnet publish src/FloatDesk.SessionAgent/FloatDesk.SessionAgent.csproj `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:NuGetAudit=false `
        "-p:Version=$Version" `
        -o $sessionAgentDirectory @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Session agent publish failed.' }

    $sessionAgentHash = Get-FileHash -Algorithm SHA256 `
        -LiteralPath (Join-Path $sessionAgentDirectory 'YueYuOrb.SessionAgent.exe')
    "$($sessionAgentHash.Hash.ToLowerInvariant())  YueYuOrb.SessionAgent.exe" |
        Set-Content `
            -LiteralPath (Join-Path $sessionAgentDirectory 'YueYuOrb.SessionAgent.exe.sha256') `
            -Encoding ascii

    dotnet publish src/FloatDesk.SessionClient/FloatDesk.SessionClient.csproj `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:NuGetAudit=false `
        "-p:Version=$Version" `
        -o $sessionClientDirectory @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Session client publish failed.' }

    dotnet publish src/FloatDesk.SessionBroker/FloatDesk.SessionBroker.csproj `
        -c Release -r win-x64 --self-contained true --no-restore `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        -p:NuGetAudit=false `
        "-p:Version=$Version" `
        -o $sessionBrokerDirectory @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Session broker publish failed.' }

    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') `
        -Destination (Join-Path $publishDirectory 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $root 'README.md') `
        -Destination (Join-Path $publishDirectory 'README.md')
    $docsDirectory = Join-Path $publishDirectory 'docs'
    New-Item -ItemType Directory -Force -Path $docsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs/manual-desktop-sessions.md') `
        -Destination (Join-Path $docsDirectory 'manual-desktop-sessions.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/user-guide.md') `
        -Destination (Join-Path $docsDirectory 'user-guide.md')
    Copy-Item -LiteralPath (Join-Path $root 'docs/manual-desktop-sessions.md') `
        -Destination (Join-Path $sessionAgentDirectory 'SESSION-AGENT.md')

    & (Join-Path $root 'scripts/verify-session-payload.ps1') `
        -PublishDirectory $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Session payload verification failed.' }

    Compress-Archive -Path (Join-Path $publishDirectory '*') `
        -DestinationPath $zipPath -CompressionLevel Optimal
    Compress-Archive -Path (Join-Path $sessionAgentDirectory '*') `
        -DestinationPath $sessionAgentZipPath -CompressionLevel Optimal
    $exeHash = Get-FileHash -Algorithm SHA256 `
        -LiteralPath (Join-Path $publishDirectory 'YueYuOrb.exe')
    $zipHash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
    $sessionAgentZipHash = Get-FileHash -Algorithm SHA256 `
        -LiteralPath $sessionAgentZipPath
    $hashLines = [System.Collections.Generic.List[string]]::new()
    $hashLines.Add(
        "$($exeHash.Hash.ToLowerInvariant())  publish/win-x64/YueYuOrb.exe"
    )
    $hashLines.Add(
        "$($zipHash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))"
    )
    $hashLines.Add(
        "$($sessionAgentZipHash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($sessionAgentZipPath))"
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
            "/DPublishDir=$publishDirectory" `
            "/DOutputDir=$artifactRoot" `
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
    Write-Host "Agent ZIP: $sessionAgentZipPath"
    if ($Installer) { Write-Host "Installer: $setupPath" }
    Write-Host "Hashes:    $hashPath"
}
finally {
    Pop-Location
}
