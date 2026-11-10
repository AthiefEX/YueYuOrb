param(
    [Parameter(Mandatory)][string]$RuntimeConfig,
    [Parameter(Mandatory)][string]$GeneratedConfig,
    [Parameter(Mandatory)][string]$MergeConfig,
    [Parameter(Mandatory)][string]$CorePath,
    [Parameter(Mandatory)][string]$BackupDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security

function Add-NcsiFilter([string]$text) {
    if ($text -match '(?m)^\s+fake-ip-filter:') {
        if ($text.Contains('+.msftncsi.com') -and $text.Contains('+.msftconnecttest.com')) { return $text }
        throw 'An existing Fake-IP filter needs review before adding entries.'
    }
    if ($text -match '(?m)^\s+fake-ip-filter-mode:\s*(whitelist|rule)') {
        throw 'This repair requires blacklist filtering.'
    }
    $newline = if ($text.Contains([string][char]13)) { ([string][char]13)+([string][char]10) } else { [string][char]10 }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]($text -split '\r?\n'))
    $index = -1
    for ($i=0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^dns:\s*(?:#.*)?$') { $index=$i; break }
    }
    $insert = [string[]]@('  fake-ip-filter:', "    - '+.msftncsi.com'", "    - '+.msftconnecttest.com'")
    if ($index -ge 0) { $lines.InsertRange($index+1, $insert) }
    else {
        if ($text -match '(?m)^dns:') { throw 'Inline DNS configuration needs review.' }
        $lines.Add('dns:')
        $lines.AddRange($insert)
    }
    return [string]::Join($newline, $lines)
}

function Read-Scalar([string]$text, [string]$key) {
    $match = [regex]::Match($text, '(?m)^'+[regex]::Escape($key)+':[ \t]*(.*?)[ \t]*\r?$')
    if (-not $match.Success) { return '' }
    $value = $match.Groups[1].Value
    if ($value.StartsWith('"')) { return [string]($value | ConvertFrom-Json) }
    if ($value.StartsWith("'") -and $value.EndsWith("'")) { return $value.Substring(1,$value.Length-2).Replace("''","'") }
    return $value
}

$paths = @($RuntimeConfig,$GeneratedConfig,$MergeConfig)
$original = @{}
$updated = @{}
foreach ($path in $paths) {
    $original[$path] = [IO.File]::ReadAllBytes($path)
    $updated[$path] = Add-NcsiFilter ([Text.Encoding]::UTF8.GetString($original[$path]))
}
$runtimeText = [Text.Encoding]::UTF8.GetString($original[$RuntimeConfig])
$address = Read-Scalar $runtimeText 'external-controller'
$pipeAddress = Read-Scalar $runtimeText 'external-controller-pipe'
$token = Read-Scalar $runtimeText 'secret'
$pipeName = $null
if ($address -match '^(?:127\.0\.0\.1|0\.0\.0\.0|localhost)?:([0-9]+)$') {
    $endpoint = 'http://127.0.0.1:'+$Matches[1]+'/configs?force=true'
} elseif ($pipeAddress -match '^\\\\\.\\pipe\\(.+)$') {
    $pipeName = $Matches[1]
    # The service can override the YAML pipe name on its command line.
    $ownerId = Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $RuntimeConfig))
    $available = @(Get-ChildItem -LiteralPath '\\.\pipe\' | Select-Object -ExpandProperty Name)
    if ($available -notcontains $pipeName) {
        $live = @($available | Where-Object { $_.StartsWith('verge-mihomo-', [StringComparison]::Ordinal) -and $_.EndsWith('-'+$ownerId, [StringComparison]::Ordinal) })
        if ($live.Count -ne 1) { throw 'Cannot uniquely identify this user core controller pipe.' }
        $pipeName = $live[0]
    }
    Add-Type -TypeDefinition @'
using System;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
public static class YueYuPipeReload {
    public static async Task<int> ReloadAsync(string name, string token, string body) {
        using(var handler=new SocketsHttpHandler()) {
            handler.UseProxy=false;
            handler.ConnectCallback=async (context,cancel)=>{
                var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(5000,cancel); return pipe; }
                catch { pipe.Dispose(); throw; }
            };
            using(var client=new HttpClient(handler)) {
                client.Timeout=TimeSpan.FromSeconds(15);
                using(var request=new HttpRequestMessage(HttpMethod.Put,"http://localhost/configs?force=true")) {
                    if(!string.IsNullOrEmpty(token)) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
                    request.Content=new StringContent(body,Encoding.UTF8,"application/json");
                    using(var response=await client.SendAsync(request)) return (int)response.StatusCode;
                }
            }
        }
    }
}
'@
} else { throw 'No supported local controller endpoint.' }
$headers = if ($token) { @{ Authorization='Bearer '+$token } } else { @{} }
$body = @{ path=$RuntimeConfig } | ConvertTo-Json -Compress
function Invoke-CoreReload {
    if ($pipeName) {
        $status=[YueYuPipeReload]::ReloadAsync($pipeName,$token,$body).GetAwaiter().GetResult()
        if ($status -lt 200 -or $status -ge 300) { throw ('Local core reload rejected: HTTP '+$status) }
    } else {
        Invoke-RestMethod -Uri $endpoint -Method Put -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
    }
}
New-Item -ItemType Directory -Path $BackupDirectory -Force | Out-Null
$manifest = @()
for ($i=0; $i -lt $paths.Count; $i++) {
    $encrypted = [Security.Cryptography.ProtectedData]::Protect($original[$paths[$i]],$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
    $backup = Join-Path $BackupDirectory ($i.ToString()+'.dpapi')
    [IO.File]::WriteAllBytes($backup,$encrypted)
    $manifest += @{ path=$paths[$i]; backup=$backup }
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $BackupDirectory 'manifest.json') -Encoding utf8
$staged = Join-Path (Split-Path -Parent $RuntimeConfig) ('ncsi-check-'+[guid]::NewGuid().ToString('N')+'.yaml')
$applied = $false
try {
    [IO.File]::WriteAllText($staged,$updated[$RuntimeConfig],[Text.UTF8Encoding]::new($false))
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName=$CorePath
    $info.UseShellExecute=$false
    $info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true
    $info.RedirectStandardError=$true
    foreach($arg in @('-t','-d',(Split-Path -Parent $RuntimeConfig),'-f',$staged)) { $info.ArgumentList.Add($arg) }
    $check=[Diagnostics.Process]::Start($info)
    $stdout=$check.StandardOutput.ReadToEndAsync()
    $stderr=$check.StandardError.ReadToEndAsync()
    if (-not $check.WaitForExit(30000)) { $check.Kill(); throw 'Configuration validation timed out.' }
    $null=$stdout.GetAwaiter().GetResult()
    $null=$stderr.GetAwaiter().GetResult()
    if ($check.ExitCode -ne 0) { throw 'Mihomo rejected the proposed DNS configuration.' }
    $check.Dispose()
    foreach($path in $paths) {
        $current=[IO.File]::ReadAllBytes($path)
        if ([Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($original[$path])) { throw 'Configuration changed while preparing repair; aborting.' }
    }
    $applied=$true
    foreach($path in $paths) { [IO.File]::WriteAllText($path,$updated[$path],[Text.UTF8Encoding]::new($false)) }
    Invoke-CoreReload
    Write-Output 'NCSI DNS exceptions applied and local core reloaded.'
    Write-Output ('Encrypted rollback backup: '+$BackupDirectory)
} catch {
    if ($applied) {
        foreach($path in $paths) { [IO.File]::WriteAllBytes($path,$original[$path]) }
        try { Invoke-CoreReload } catch {}
    }
    throw
} finally {
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    $token=$null
    $headers=$null
}
