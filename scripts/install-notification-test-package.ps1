[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='High')]
param([switch]$TrustLocalTestCertificate)
$ErrorActionPreference = 'Stop'
if (-not $TrustLocalTestCertificate) { throw 'Explicit -TrustLocalTestCertificate is required. This installs a local test signing certificate and the notification MSIX.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated Windows PowerShell session.' }
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\danmaku-light'
$package = Join-Path $output 'YueYuOrb-notifications-win-x64.msix'
if (-not (Test-Path -LiteralPath $package)) { throw 'Build the notification package first.' }
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory | Sort-Object Name -Descending | Where-Object { Test-Path (Join-Path $_.FullName 'x64\signtool.exe') } | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK signtool is required.' }
if (-not $PSCmdlet.ShouldProcess('This computer', 'Create a local code-signing certificate, trust it in LocalMachine TrustedPeople (not Root), sign and install YueYuOrb.Notifications')) { return }
$certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=YueYuOrb.Local' -FriendlyName 'YueYuOrb local notification test' -KeyUsage DigitalSignature -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddMonths(6) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
$publicCertificate = Join-Path $output 'YueYuOrb.Local.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
& (Join-Path $sdk.FullName 'x64\signtool.exe') sign /fd SHA256 /sha1 $certificate.Thumbprint $package
if ($LASTEXITCODE -ne 0) { throw 'Signing failed. No trust certificate has been imported.' }
Import-Certificate -FilePath $publicCertificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Add-AppxPackage -Path $package
$certificate.Thumbprint | Set-Content -LiteralPath (Join-Path $output 'local-test-certificate-thumbprint.txt') -Encoding ascii
Write-Output "Installed local test package. Certificate thumbprint: $($certificate.Thumbprint)"
Write-Output 'Exit the portable app, then open the notification edition from Start and explicitly grant notification access.'
