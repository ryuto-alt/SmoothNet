<#
  Builds, signs and publishes a SmoothNet release on GitHub.
  Usage:  pwsh -File release.ps1 -Version 1.2.0 -Notes "changelog text"  [-AllowDirty] [-DryRun]
  Needs: the signing key (tools\New-SigningKey.ps1), gh CLI logged in, a clean + pushed git tree.
  Assets: SmoothNet.dll, SmoothNet.dll.sig, latest.json, SmoothNet-<ver>.zip (the installer exe is never uploaded).
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Notes,
    [string]$Repo = 'ryuto-alt/SmoothNet',
    [switch]$AllowDirty,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$root   = $PSScriptRoot
$dotnet = 'C:\Users\ryuto\.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Set-Location $root

# 1. version consistency
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z: $Version" }
$plugin = (Get-Content "$root\Plugin.cs" -Raw)
if ($plugin -notmatch 'Version\s*=\s*"([^"]+)"') { throw 'Version not found in Plugin.cs' }
$vPlugin = $Matches[1]
$vCsproj = ([xml](Get-Content "$root\SmoothNet.csproj" -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$vUpd    = ([xml](Get-Content "$root\Updater\SmoothNetUpdater.csproj" -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$vManif  = (Get-Content "$root\package\manifest.json" -Raw | ConvertFrom-Json).version_number
foreach ($pair in @(@('Plugin.cs', $vPlugin), @('SmoothNet.csproj', $vCsproj), @('SmoothNetUpdater.csproj', $vUpd), @('package\manifest.json', $vManif))) {
    if ($pair[1] -ne $Version) { throw "$($pair[0]) has version $($pair[1]) but release is $Version" }
}
Write-Host "versions match: $Version"

# 2. git state
if (-not $AllowDirty) {
    $dirty = git status --porcelain
    if ($dirty) { throw "Working tree not clean (commit first, or pass -AllowDirty):`n$dirty" }
}
$keyPath = Join-Path $env:LOCALAPPDATA 'SmoothNetInstaller\build\update_signing_key.xml'
if (-not (Test-Path $keyPath)) { throw "Signing key not found: $keyPath (run tools\New-SigningKey.ps1)" }

# 3. build
& $dotnet build "$root\SmoothNet.csproj" -c Release -v minimal; if ($LASTEXITCODE -ne 0) { throw 'plugin build failed' }
& $dotnet build "$root\Updater\SmoothNetUpdater.csproj" -c Release -v minimal; if ($LASTEXITCODE -ne 0) { throw 'patcher build failed' }
$dll     = "$root\bin\Release\netstandard2.1\SmoothNet.dll"
$updater = "$root\Updater\bin\Release\netstandard2.0\SmoothNetUpdater.dll"

# 4. sign (RSA PKCS#1 v1.5 / SHA-256)
$out = "$root\dist\v$Version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item $dll "$out\SmoothNet.dll"
$bytes = [IO.File]::ReadAllBytes("$out\SmoothNet.dll")
$rsa = [Security.Cryptography.RSA]::Create()
$rsa.FromXmlString([IO.File]::ReadAllText($keyPath))
$sig = $rsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
[IO.File]::WriteAllBytes("$out\SmoothNet.dll.sig", $sig)
# self-check with the public part only
$pub = [Security.Cryptography.RSA]::Create(); $pub.ImportParameters($rsa.ExportParameters($false))
if (-not $pub.VerifyData($bytes, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'self-verification failed' }
$sha = [BitConverter]::ToString([Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '').ToLowerInvariant()
$rsa.Dispose()

# 5. latest.json
$base = "https://github.com/$Repo/releases/download/v$Version"
$latest = [ordered]@{ version = $Version; dll = "$base/SmoothNet.dll"; sig = "$base/SmoothNet.dll.sig"; sha256 = $sha }
[IO.File]::WriteAllText("$out\latest.json", ($latest | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))

# 6. Thunderstore-style zip (mod at root, patcher under patchers/)
Copy-Item $dll "$root\package\SmoothNet.dll" -Force
Copy-Item $updater "$root\package\SmoothNetUpdater.dll" -Force
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = "$out\SmoothNet-$Version.zip"
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($e in @(@('icon.png', "$root\package\icon.png"), @('manifest.json', "$root\package\manifest.json"), @('README.md', "$root\package\README.md"),
                     @('SmoothNet.dll', "$out\SmoothNet.dll"), @('patchers/SmoothNetUpdater.dll', $updater))) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $e[1], $e[0], 'Optimal')
    }
} finally { $zip.Dispose() }
Copy-Item $zipPath "$root\dist\SmoothNet-$Version.zip" -Force
Write-Host "built $out  (sha256 $sha)"

if ($DryRun) { Write-Host 'dry run: not publishing'; return }

# 7. publish
$assets = @("$out\SmoothNet.dll", "$out\SmoothNet.dll.sig", "$out\latest.json", $zipPath)
gh release create "v$Version" @assets --repo $Repo --title "SmoothNet $Version" --notes $Notes --target main
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
Write-Host "released: https://github.com/$Repo/releases/tag/v$Version"
