param(
    [Parameter(Mandatory=$true)][string]$ScriptPath,
    [Parameter(Mandatory=$true)][Guid]$EnvironmentId,
    [string]$OutputPath = '.\desktop-task.json',
    [switch]$GuestAdministrator
)
$ErrorActionPreference='Stop'
$content=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $ScriptPath).Path)
$sha=[Security.Cryptography.SHA256]::Create()
try { $digest=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($content)))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
$name=[IO.Path]::GetFileName($ScriptPath)
$kind=if($GuestAdministrator){'RunSystemScript'}else{'RunScript'}
$task=[ordered]@{
    formatVersion=1;id=[guid]::NewGuid().ToString();name='脚本任务';environmentId=$EnvironmentId.ToString()
    steps=@(@{id=[guid]::NewGuid().ToString();kind=$kind;parameters=@{scriptName=$name;arguments='[]'};timeoutSeconds=120;retryCount=0})
    trigger=@{kind='Manual';localTime=$null;timeZoneId=$null};revision=1;enabled=$false
    rules='先检查目标环境，再运行；失败后核对实际状态。'
    scripts=@(@{name=$name;content=$content;sha256=$digest})
}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($task|ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
Write-Host '任务 JSON 已生成；请在月语导入、检查并应用。不会自动执行。'
