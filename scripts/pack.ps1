<#
.SYNOPSIS
  Build a Release MSIX of the extension, optionally signed with a self-signed certificate.

.DESCRIPTION
  Output goes to dist\. Without -Sign the package is unsigned and only useful for
  inspection. With -Sign, a code-signing certificate whose subject matches the
  manifest Publisher (CN=NowPlayingDev) is created in CurrentUser\My on first use,
  its public part is exported to dist\NowPlayingDev.cer, and the MSIX is signed.

  To install a self-signed package, the target machine must trust the .cer once
  (elevated):  Import-Certificate dist\NowPlayingDev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople

.PARAMETER Platform
  x64 or ARM64.
#>
[CmdletBinding()]
param(
  [ValidateSet('x64', 'ARM64')] [string]$Platform = 'x64',
  [switch]$Sign
)

$ErrorActionPreference = 'Stop'
$root    = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\NowPlaying\NowPlaying.csproj'
$dist    = Join-Path $root 'dist'
$subject = 'CN=NowPlayingDev'

dotnet publish $project -c Release -p:Platform=$Platform -p:PublishProfile=win-$($Platform.ToLower()) `
  -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false -p:AppxPackageDir="$dist\"
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)" }

$msix = Get-ChildItem $dist -Recurse -Filter "*_$Platform.msix" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No MSIX produced under $dist" }

if ($Sign) {
  $cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
  if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
      -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
      -TextExtension @('2.5.29.19={text}')
    Write-Host "Created signing certificate $($cert.Thumbprint)"
  }
  Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'NowPlayingDev.cer') | Out-Null

  $signtool = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Filter signtool.exe |
    Where-Object { $_.DirectoryName -like '*\x64' } | Select-Object -First 1
  if (-not $signtool) { throw 'signtool.exe not found; run a build first so the SDK.BuildTools package is restored.' }

  & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix.FullName
  if ($LASTEXITCODE -ne 0) { throw "Signing failed ($LASTEXITCODE)" }
}

Write-Host "Package: $($msix.FullName)"
