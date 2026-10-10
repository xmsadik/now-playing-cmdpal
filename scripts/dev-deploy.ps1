<#
.SYNOPSIS
  Build the extension and register it with Windows as an unsigned dev package.

.DESCRIPTION
  Requires Developer Mode. Re-running replaces the previous registration.
  After deploying, run "Reload" in Command Palette to pick up the new build.

.PARAMETER Remove
  Unregister the dev package and exit.
#>
[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
  [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\NowPlaying\NowPlaying.csproj'

$existing = Get-AppxPackage -Name 'NowPlaying'
if ($existing) {
  # The host keeps the COM server alive; stop it so the files can be replaced.
  Get-Process -Name 'NowPlaying' -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-AppxPackage -Package $existing.PackageFullName
  Write-Host "Removed $($existing.PackageFullName)"
}
if ($Remove) { return }

dotnet build $project -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

$manifest = Join-Path $PSScriptRoot "..\src\NowPlaying\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml"
Add-AppxPackage -Register (Resolve-Path $manifest)
Get-AppxPackage -Name 'NowPlaying' | Select-Object Name, Version, InstallLocation
Write-Host 'Deployed. In Command Palette, run "Reload" to load the new build.'
