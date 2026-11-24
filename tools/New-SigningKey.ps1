<#
  Generates the RSA-3072 update signing key pair (run once).
  Private key -> %LOCALAPPDATA%\SmoothNetInstaller\build\update_signing_key.xml (ACL: current user only; never commit/print).
  Prints the PUBLIC key (modulus/exponent, base64) to paste into Updater\UpdateCore.cs.
#>
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'SmoothNetInstaller\build'
$path = Join-Path $dir 'update_signing_key.xml'
if ((Test-Path $path) -and -not $Force) { throw "Key already exists: $path (use -Force to replace; this invalidates all shipped clients)" }
New-Item -ItemType Directory -Force $dir | Out-Null
$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$xml = $rsa.ToXmlString($true)
[System.IO.File]::WriteAllText($path, $xml, (New-Object System.Text.UTF8Encoding($false)))
& icacls.exe $path /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls failed' }
$p = $rsa.ExportParameters($false)
Write-Host 'PublicModulusB64  = ' ([Convert]::ToBase64String($p.Modulus))
Write-Host 'PublicExponentB64 = ' ([Convert]::ToBase64String($p.Exponent))
Write-Host "Private key saved: $path"
