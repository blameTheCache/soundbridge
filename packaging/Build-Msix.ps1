<#
.SYNOPSIS
    Packs dist\SoundBridge-package into a signed .msix that installs without Developer Mode.

.DESCRIPTION
    Run .\build.ps1 first. This script then:
      1. finds, or creates, a self-signed code-signing certificate whose subject matches the Publisher in
         AppxManifest.xml. It lives in your personal certificate store and its private key never leaves it;
      2. packs the folder with MakeAppx and signs it with SignTool (both from the Windows SDK);
      3. writes dist\SoundBridge-<version>.msix and dist\SoundBridge.cer (the public certificate only).

    Installing on another PC (once per PC, the first line elevated):
        Import-Certificate -FilePath .\SoundBridge.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
        Add-AppxPackage -Path .\SoundBridge-<version>.msix

    A self-signed certificate proves only that two packages came from the same key, not who made them.
    Anyone installing must decide to trust it. A certificate from a public authority removes that step.

    The signature is not timestamped, so the package stops installing when the certificate expires.
#>
param([int]$ValidYears = 3)

$ErrorActionPreference = 'Stop'

# New-SelfSignedCertificate and the certificate drive behave most predictably in Windows PowerShell.
if ($PSVersionTable.PSEdition -eq 'Core') {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -ValidYears $ValidYears
    exit $LASTEXITCODE
}

$root = Split-Path $PSScriptRoot -Parent
$layout = Join-Path $root 'dist\SoundBridge-package'
$manifestPath = Join-Path $layout 'AppxManifest.xml'
if (-not (Test-Path $manifestPath)) { throw "Nothing to pack at $layout. Run .\build.ps1 first." }

[xml]$manifest = Get-Content $manifestPath
$publisher = $manifest.Package.Identity.Publisher
$version = $manifest.Package.Identity.Version

function Find-SdkTool([string]$name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem (Join-Path $kits "10.*\x64\$name") -ErrorAction SilentlyContinue | Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
    if (-not $tool) { throw "$name was not found. Install the Windows SDK (it ships with Visual Studio's desktop workloads)." }
    $tool.FullName
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } |
    Sort-Object NotAfter | Select-Object -Last 1

if (-not $certificate) {
    Write-Host "Creating a self-signed signing certificate for $publisher in your personal store."
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher -FriendlyName 'SoundBridge package signing' `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears($ValidYears) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

$msix = Join-Path $root "dist\SoundBridge-$version.msix"
$cer = Join-Path $root 'dist\SoundBridge.cer'

& $makeAppx pack /d $layout /p $msix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed. Run it by hand to see why: the manifest is the usual cause.' }

& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }

Export-Certificate -Cert $certificate -FilePath $cer | Out-Null

$signature = Get-AuthenticodeSignature $msix
Write-Host "`nPacked and signed:" -ForegroundColor Green
Write-Host "  $msix"
Write-Host "  $cer   (public certificate; share it with the package)"
Write-Host "  signed by $($signature.SignerCertificate.Subject), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "`nTo install on a PC that does not have the certificate yet, see the notes at the top of this script."
