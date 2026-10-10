<#
.SYNOPSIS
    Runs the tests and publishes SoundBridge.

.DESCRIPTION
    Produces two folders:
      dist\SoundBridge          the plain exe. Run it from anywhere. Music only.
      dist\SoundBridge-package  the same exe laid out as a Windows package. Install it with
                                packaging\Install.ps1 to get phone calls as well.

.PARAMETER SelfContained
    Bundle the .NET runtime so the target PC needs nothing installed (much larger output).
    Without it, the PC needs the .NET 10 Desktop Runtime.

.PARAMETER Runtime
    win-x64 (default) or win-arm64.
#>
param(
    [switch]$SelfContained,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A running copy keeps its exe locked, and the publish would fail half-way through.
$dist = Join-Path $PSScriptRoot 'dist'
$running = Get-Process SoundBridge -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw 'SoundBridge is running from the dist folder. Exit it from the tray menu first.' }

dotnet test tests/SoundBridge.Core.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

$output = Join-Path $dist 'SoundBridge'
dotnet publish src/SoundBridge -c Release -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item LICENSE, NOTICE.md, README.md -Destination $output -Force

# The package layout: the published exe plus the manifest and the logos it names.
$package = Join-Path $dist 'SoundBridge-package'
New-Item -ItemType Directory -Force (Join-Path $package 'Images') | Out-Null
Copy-Item (Join-Path $output 'SoundBridge.exe') -Destination $package -Force
Copy-Item packaging\Images\*.png -Destination (Join-Path $package 'Images') -Force
$architecture = $Runtime -replace '^win-', ''
(Get-Content packaging\AppxManifest.xml -Raw) -replace 'ProcessorArchitecture="x64"', "ProcessorArchitecture=`"$architecture`"" |
    Set-Content (Join-Path $package 'AppxManifest.xml') -Encoding utf8

Write-Host "`nPublished:" -ForegroundColor Green
Write-Host "  $output            plain exe (music only)"
Write-Host "  $package    install with packaging\Install.ps1 (music and phone calls)"
