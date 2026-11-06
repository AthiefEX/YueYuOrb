param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$resolved = [System.IO.Path]::GetFullPath($PublishDirectory)
if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
    throw "Publish directory does not exist: $resolved"
}

$required = @(
    'YueYuOrb.exe',
    'desktop-sessions/agent/YueYuOrb.SessionAgent.exe',
    'desktop-sessions/agent/YueYuOrb.SessionAgent.exe.sha256',
    'desktop-sessions/client/YueYuOrb.SessionClient.exe',
    'desktop-sessions/broker/YueYuOrb.SessionBroker.exe',
    'LICENSE',
    'README.md',
    'docs/manual-desktop-sessions.md',
    'docs/user-guide.md'
)

$missing = [System.Collections.Generic.List[string]]::new()
foreach ($relative in $required) {
    $path = Join-Path $resolved $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $missing.Add($relative)
    }
}
if ($missing.Count -gt 0) {
    throw "Session payload is incomplete: $($missing -join ', ')"
}

$agent = Join-Path $resolved 'desktop-sessions/agent/YueYuOrb.SessionAgent.exe'
$manifest = Get-Content -LiteralPath ($agent + '.sha256') -Raw
$expected = ([regex]::Match($manifest, '(?i)\b[0-9a-f]{64}\b')).Value
if ([string]::IsNullOrWhiteSpace($expected)) {
    throw 'Session agent manifest does not contain a SHA256 hash.'
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $agent).Hash
if (-not [string]::Equals($expected, $actual, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Session agent hash mismatch: expected $expected, actual $actual"
}

Write-Output "Session payload verified: $resolved"
Write-Output "Required files: $($required.Count)"
Write-Output "Session agent SHA256: $($actual.ToLowerInvariant())"
