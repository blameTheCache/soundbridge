<#
.SYNOPSIS
    Runs the tests and publishes SoundBridge to .\dist\SoundBridge.

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

dotnet test tests/SoundBridge.Core.Tests -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

$output = Join-Path $PSScriptRoot 'dist\SoundBridge'
dotnet publish src/SoundBridge -c Release -r $Runtime `
    --self-contained:$($SelfContained.IsPresent) `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Copy-Item LICENSE, NOTICE.md, README.md -Destination $output -Force
Write-Host "`nPublished to $output" -ForegroundColor Green
Get-ChildItem $output | Select-Object Name, @{ n = 'Size (KB)'; e = { [math]::Round($_.Length / 1KB) } } | Format-Table -AutoSize
