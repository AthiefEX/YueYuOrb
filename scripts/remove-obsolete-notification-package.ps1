param(
    [switch]$Apply,
    [string]$MsixPath = '',
    [string]$RecoveryDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw '请使用 Windows PowerShell 5.1 执行此脚本。'
}
$root = Split-Path -Parent $PSScriptRoot
if (!$MsixPath) { $MsixPath = Join-Path $root 'artifacts\danmaku-light\YueYuOrb-notifications-win-x64.msix' }
if (!$RecoveryDirectory) { $RecoveryDirectory = Join-Path $root ('artifacts\obsolete-notification-recovery\' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$expectedFullName = 'YueYuOrb.Notifications_1.2.0.4_x64__t46e76va7zvsc'
$packages = @(Get-AppxPackage -Name 'YueYuOrb.Notifications')
if ($packages.Count -eq 0) { Write-Output '旧版通知实验包未注册；无需修复。'; exit 0 }
if ($packages.Count -ne 1 -or $packages[0].PackageFullName -ne $expectedFullName -or
    $packages[0].Publisher -ne 'CN=YueYuOrb.Local' -or $packages[0].IsFramework) {
    throw '检测到不同通知包版本或身份；拒绝自动移除。'
}
$package = $packages[0]
[pscustomobject]@{ Package=$package.PackageFullName; Action='仅注销当前用户旧版通知实验包；保留便携程序与FloatDesk配置'; Apply=[bool]$Apply } | Format-List
if (!$Apply) { exit 0 }

# Require a verified recovery archive before modifying the package registration.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $MsixPath).Path)
try {
    $reader = New-Object IO.StreamReader($archive.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -ne 'YueYuOrb.Notifications' -or $identity.Version -ne '1.2.0.4' -or
        $identity.Publisher -ne 'CN=YueYuOrb.Local' -or $identity.ProcessorArchitecture -ne 'x64') {
        throw '恢复安装包身份不匹配；拒绝移除。'
    }
} finally { $archive.Dispose() }
New-Item -ItemType Directory -Path $RecoveryDirectory -Force | Out-Null
$backup = Join-Path $RecoveryDirectory 'YueYuOrb-notifications-win-x64.msix'
Copy-Item -LiteralPath $MsixPath -Destination $backup
$hash = (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash
if ($hash -ne (Get-FileHash -LiteralPath $MsixPath -Algorithm SHA256).Hash) { throw '恢复包复制校验失败。' }
Copy-Item -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -Destination (Join-Path $RecoveryDirectory 'AppxManifest.xml')
[pscustomobject]@{Package=$expectedFullName; ArchiveSHA256=$hash; Archive=$backup; Restore='Add-AppxPackage -Path <备份msix完整路径>'; RemovedAt=(Get-Date -Format o)} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RecoveryDirectory 'recovery.json') -Encoding UTF8
Remove-AppxPackage -Package $expectedFullName -ErrorAction Stop
if (Get-AppxPackage -Name 'YueYuOrb.Notifications') { throw '旧包仍注册，修复未完成。' }
Write-Output ('旧通知实验包已注销。恢复安装包：' + $backup)
