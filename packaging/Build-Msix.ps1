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

# Pack from a fresh folder holding exactly the files a package is meant to have. The layout folder is reused
# from build to build, and Windows adds files of its own once it is registered; whatever else has found its
# way in there must not end up inside a signed package.
$expected = 'AppxManifest.xml', 'SoundBridge.exe', 'Images\Square150x150Logo.png', 'Images\Square44x44Logo.png', 'Images\StoreLogo.png'
$staging = Join-Path ([IO.Path]::GetTempPath()) "SoundBridge-msix-$([guid]::NewGuid().ToString('N'))"
try {
    foreach ($file in $expected) {
        $source = Join-Path $layout $file
        if (-not (Test-Path $source -PathType Leaf)) { throw "Missing from the package layout: $file. Run .\build.ps1 again." }
        $target = Join-Path $staging $file
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }

    $extra = Get-ChildItem $layout -Recurse -File | ForEach-Object { $_.FullName.Substring($layout.Length + 1) } | Where-Object { $_ -notin $expected }
    if ($extra) { Write-Warning "Left out of the package, because these are not part of it: $($extra -join ', ')" }

    & $makeAppx pack /d $staging /p $msix /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed. Run it by hand to see why: the manifest is the usual cause.' }
}
finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}

& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed.' }

Export-Certificate -Cert $certificate -FilePath $cer | Out-Null

$signature = Get-AuthenticodeSignature $msix
Write-Host "`nPacked and signed:" -ForegroundColor Green
Write-Host "  $msix"
Write-Host "  $cer   (public certificate; share it with the package)"
Write-Host "  signed by $($signature.SignerCertificate.Subject), valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "`nTo install on a PC that does not have the certificate yet, see the notes at the top of this script."
