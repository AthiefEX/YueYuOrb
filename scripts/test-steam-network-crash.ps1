param([Parameter(Mandatory=$true)][string]$Executable,[switch]$RecoverOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$Executable=[IO.Path]::GetFullPath($Executable)
$evidence=Join-Path $repo 'artifacts/steam-compatibility-native'
$journal=Join-Path $evidence 'lease/steam-network-trust.json'
$hostsFile=Join-Path ([Environment]::GetFolderPath('System')) 'drivers/etc/hosts'
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class YueYuOwnedRootDialog {
 public delegate bool EnumProc(IntPtr hwnd,IntPtr extra);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr extra);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent,EnumProc callback,IntPtr extra);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hwnd);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int count);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
}
'@
function Confirm-TestRootRemoval([int]$TestProcessId,$RootRecord) {
    [void][YueYuOwnedRootDialog]::EnumWindows({param($window,$extra)
        $owner=[uint32]0;[void][YueYuOwnedRootDialog]::GetWindowThreadProcessId($window,[ref]$owner)
        if($owner -ne $TestProcessId){return $true}
        $script:ownedRootText='';$script:ownedRootYes=[IntPtr]::Zero
        [void][YueYuOwnedRootDialog]::EnumChildWindows($window,{param($child,$state)
            $text=[Text.StringBuilder]::new(4096);[void][YueYuOwnedRootDialog]::GetWindowText($child,$text,4096)
            $script:ownedRootText+=$text.ToString()
            if([YueYuOwnedRootDialog]::GetDlgCtrlID($child) -eq 6){$script:ownedRootYes=$child}
            return $true
        },[IntPtr]::Zero)
        if(($script:ownedRootText.Contains('删除') -or $script:ownedRootText.ToLowerInvariant().Contains('delete')) -and
           $script:ownedRootText.Contains($RootRecord.Subject.Substring(3)) -and
           $script:ownedRootText.Replace(' ','').Contains($RootRecord.Thumbprint) -and $script:ownedRootYes -ne [IntPtr]::Zero){
            [void][YueYuOwnedRootDialog]::PostMessage($script:ownedRootYes,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
        }
        return $true
    },[IntPtr]::Zero)
}
$beforeHash=(Get-FileHash -LiteralPath $hostsFile).Hash
if(-not $RecoverOnly){
$probe=Start-Process -FilePath $Executable -ArgumentList '--steam-network-selftest','hold' -WorkingDirectory $repo -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(40)
$ready=$false
while([DateTime]::UtcNow -lt $deadline -and -not $probe.HasExited){
    $readyFile=Join-Path $evidence 'hold.json'
    if(Test-Path -LiteralPath $readyFile){
        $state=Get-Content -Raw -LiteralPath $readyFile|ConvertFrom-Json
        if($state.pid -eq $probe.Id -and $state.ready){$ready=$true;break}
    }
    Start-Sleep -Milliseconds 200
}
if(-not $ready){throw 'Crash probe did not reach its test lease; no process was terminated.'}
$record=Get-Content -Raw -LiteralPath $journal|ConvertFrom-Json
$owned=Get-Process -Id $probe.Id
if($owned.StartTime.ToUniversalTime() -ne $probe.StartTime.ToUniversalTime() -or $owned.Path -ne $Executable){throw 'Test identity changed; no process was terminated.'}
Stop-Process -Id $probe.Id -Force
$deadline=[DateTime]::UtcNow.AddSeconds(12)
do {
    $restored=(Get-FileHash -LiteralPath $hostsFile).Hash -eq $beforeHash
    if($restored){break};Start-Sleep -Milliseconds 200
} while([DateTime]::UtcNow -lt $deadline)
if(-not $restored){throw 'The test helper did not restore hosts after the test parent exited.'}
}else{
    $record=Get-Content -Raw -LiteralPath $journal|ConvertFrom-Json
    $restored=-not ([IO.File]::ReadAllText($hostsFile)).Contains('# BEGIN YueYuOrb Steam ')
}
$recovery=Start-Process -FilePath $Executable -ArgumentList '--steam-network-selftest','recover' -WorkingDirectory $repo -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(40)
while([DateTime]::UtcNow -lt $deadline -and -not $recovery.HasExited){
    Confirm-TestRootRemoval $recovery.Id $record
    Start-Sleep -Milliseconds 200
}
if(-not $recovery.HasExited){throw 'Recovery remains pending; its process was left running to preserve cleanup.'}
$recovered=Get-Content -Raw -LiteralPath (Join-Path $evidence 'recover.json')|ConvertFrom-Json
$result=[ordered]@{HostsRestoredAfterCrash=$restored;KeysRemovedOnRecovery=$recovered.temporaryKeysRemoved;JournalRemoved=$recovered.recoveryJournalRemoved;RecoveryCompleted=$recovered.completed;ProxyUnchanged=$recovered.proxyUnchanged}
$result|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'crash-cleanup.json') -Encoding UTF8
$result|ConvertTo-Json
if(-not $recovered.completed -or -not $recovered.temporaryKeysRemoved -or -not $recovered.recoveryJournalRemoved){throw 'Interrupted-lifetime recovery failed.'}
