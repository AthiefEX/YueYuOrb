param([string]$CertificateThumbprint = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\danmaku-light'
$publish = Join-Path $output 'win-x64'
$layout = Join-Path $output 'package-layout'
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Sort-Object Name -Descending | Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK makeappx is required.' }
New-Item -ItemType Directory -Force -Path $publish,$layout,(Join-Path $layout 'Assets') | Out-Null
dotnet publish (Join-Path $root 'src\FloatDesk.App') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -p:NuGetAudit=false -p:UseArtifactsOutput=true "-p:ArtifactsPath=$root\artifacts\build" -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -Path (Join-Path $publish '*') -Destination $layout -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\notifications\AppxManifest.xml') -Destination (Join-Path $layout 'AppxManifest.xml') -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $layout -Force
Add-Type -AssemblyName System.Drawing
foreach ($entry in @(@('StoreLogo.png',50), @('Square150x150Logo.png',150), @('Square44x44Logo.png',44))) {
    $size = [int]$entry[1]
    $bitmap = New-Object System.Drawing.Bitmap($size,$size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $blue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(44,119,203))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(232,246,255))
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.FillEllipse($blue,0,0,($size-1),($size-1))
        $graphics.FillEllipse($white,($size*.2),($size*.15),($size*.65),($size*.65))
        $graphics.FillEllipse($blue,($size*.38),($size*.08),($size*.53),($size*.57))
        $bitmap.Save((Join-Path $layout "Assets\$($entry[0])"),[System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $blue.Dispose(); $white.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$package = Join-Path $output 'YueYuOrb-notifications-win-x64.msix'
& (Join-Path $sdk.FullName 'x64\makeappx.exe') pack /d $layout /p $package /o | Out-File -LiteralPath (Join-Path $output 'makeappx.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'makeappx.log') -Tail 12; throw 'MSIX validation failed.' }
if ($CertificateThumbprint) {
    & (Join-Path $sdk.FullName 'x64\signtool.exe') sign /fd SHA256 /sha1 $CertificateThumbprint $package
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed. Certificate subject must match the manifest Publisher.' }
}
Write-Output "Notification package: $package"
if (-not $CertificateThumbprint) { Write-Output 'Unsigned package: trusted signing is required before installation. No certificate or app has been installed.' }
