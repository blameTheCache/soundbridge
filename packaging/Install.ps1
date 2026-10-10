<#
.SYNOPSIS
    Installs SoundBridge for the current user from dist\SoundBridge-package, so that it can route phone calls.

.DESCRIPTION
    Registers the folder with Windows in place (no .msix, no certificate). That needs Developer Mode:
    Settings > System > Advanced (or "For developers") > Developer Mode. No administrator rights are used.

    The folder is not copied anywhere. Keep dist\SoundBridge-package where it is while SoundBridge is installed.

.PARAMETER Uninstall
    Removes the registration. Settings and logs are left alone.
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

# The Appx cmdlets load natively only in Windows PowerShell.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Uninstall) { $forward += '-Uninstall' }
    & powershell.exe @forward
    exit $LASTEXITCODE
}

$name = 'SoundBridge'
$layout = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\SoundBridge-package'

if ($Uninstall) {
    $installed = Get-AppxPackage -Name $name
    if (-not $installed) { Write-Host 'SoundBridge is not installed.'; exit 0 }
    $installed | Remove-AppxPackage
    Write-Host 'SoundBridge was uninstalled. Settings and logs in %LOCALAPPDATA%\SoundBridge were kept.'
    exit 0
}

if (-not (Test-Path (Join-Path $layout 'SoundBridge.exe'))) {
    throw "Nothing to install at $layout. Run .\build.ps1 first."
}

if (Get-Process SoundBridge -ErrorAction SilentlyContinue) {
    throw 'SoundBridge is running. Exit it from the tray menu first, then run this again.'
}

$developerMode = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense
if ($developerMode -ne 1) {
    throw 'Developer Mode is off. Turn it on in Settings > System > Advanced (or "For developers"), then run this again.'
}

Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ForceUpdateFromAnyVersion
$package = Get-AppxPackage -Name $name
Write-Host "Installed $($package.PackageFullName)." -ForegroundColor Green
Write-Host 'Start SoundBridge from the Start menu. Started any other way it runs without its package identity and cannot route calls.'
