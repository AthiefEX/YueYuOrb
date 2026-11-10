param(
    [ValidateSet('Preview', 'Apply', 'Restore')][string]$Mode = 'Preview',
    [string]$ExpectedHash = '',
    [string]$BackupPath = ''
)

$ErrorActionPreference = 'Stop'
$taskName = 'Clash Verge (Admin)'
$target = 'D:\Clash Verge\clash-verge.exe'
$protectedRoot = Join-Path $env:ProgramFiles 'YueYuOrb\desktop-sessions'
$guard = Join-Path $protectedRoot 'agent\YueYuOrb.SessionAgent.exe'
$backupRoot = Join-Path $protectedRoot 'startup-guards'
$markerPath = Join-Path $backupRoot 'Clash-Verge-Admin.active.json'
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$folder = $scheduler.GetFolder('\')
$task = $folder.GetTask($taskName)
$original = [string]$task.Xml
function Get-DefinitionHash([string]$Xml) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Xml)))).Replace('-', '') }
    finally { $sha.Dispose() }
}
$currentHash = Get-DefinitionHash $original
$definition = $scheduler.NewTask(0)
$definition.XmlText = $original
if ($definition.Actions.Count -ne 1 -or $definition.Principal.LogonType -ne 3) {
    throw 'Only the verified single-action interactive task is supported.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$taskUser = New-Object Security.Principal.NTAccount($definition.Principal.UserId)
if ($taskUser.Translate([Security.Principal.SecurityIdentifier]).Value -ne $identity.User.Value) {
    throw 'The task owner does not match the current user.'
}
if ($Mode -ne 'Preview' -and $ExpectedHash -ne $currentHash) {
    throw 'Task changed since review. Refusing to overwrite it.'
}
$action = $definition.Actions.Item(1)
if ($Mode -eq 'Restore') {
    $resolvedBackup = [IO.Path]::GetFullPath($BackupPath)
    if (-not $resolvedBackup.StartsWith([IO.Path]::GetFullPath($backupRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backup must be inside the protected startup-guards directory.'
    }
    if ($action.Path -ne $guard) { throw 'Current task no longer uses the guarded action.' }
    $definition.XmlText = [IO.File]::ReadAllText($resolvedBackup)
    if ($definition.Actions.Count -ne 1 -or $definition.Actions.Item(1).Path -ne $target -or $definition.Principal.LogonType -ne 3) {
        throw 'Backup does not match the verified original task.'
    }
} else {
    if ($action.Path -ne $target) { throw 'Task executable differs from the reviewed Clash path.' }
    if ($action.Arguments -match '(?i)token|password|secret|subscription|https?://') {
        throw 'Task arguments need separate review; no backup or change was made.'
    }
    $originalArguments = [string]$action.Arguments
    $action.Path = $guard
    $action.Arguments = '--guard-startup-app "' + $target + '" -- ' + $originalArguments
}
if ($Mode -eq 'Preview') {
    [pscustomobject]@{
        Task = $taskName; BaselineSha256 = $currentHash; OriginalExecutable = $target
        GuardExecutable = $guard; ArgumentsPreserved = $true; TriggersPreserved = $true
        PrincipalPreserved = $true; SystemChangeMade = $false
    } | ConvertTo-Json -Compress
    exit 0
}
if ($Mode -eq 'Apply') {
    if (-not (Test-Path -LiteralPath $guard)) { throw 'Install the updated guard Agent first.' }
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $BackupPath = Join-Path $backupRoot ('Clash-Verge-Admin.' + $currentHash + '.xml')
    if (Test-Path -LiteralPath $BackupPath) {
        if ((Get-DefinitionHash ([IO.File]::ReadAllText($BackupPath))) -ne $currentHash) { throw 'Existing backup mismatch.' }
    } else {
        [IO.File]::WriteAllText($BackupPath, $original, [Text.Encoding]::Unicode)
    }
}
$null = $folder.RegisterTaskDefinition($taskName, $definition, 4, $null, $null, 3, $null)
$applied = $folder.GetTask($taskName)
if ($Mode -eq 'Apply') {
    [pscustomobject]@{ Task = $taskName; Backup = $BackupPath; AppliedSha256 = (Get-DefinitionHash ([string]$applied.Xml)) } |
        ConvertTo-Json | Set-Content -LiteralPath $markerPath -Encoding UTF8
} elseif (Test-Path -LiteralPath $markerPath) {
    Remove-Item -LiteralPath $markerPath
}
[pscustomobject]@{ Mode = $Mode; Task = $taskName; Backup = $BackupPath; AppliedSha256 = (Get-DefinitionHash ([string]$applied.Xml)) } | ConvertTo-Json -Compress
