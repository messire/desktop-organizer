<#
.SYNOPSIS
    Builds the Microsoft Store MSIX package (installer\Output\DesktopOrganizer-<version>.msix) without Visual Studio.

.DESCRIPTION
    Pipeline (docs/PLANS/store-publication-implementation.md, Package 4):
      1. installer\build.ps1 -SkipInstaller      - self-contained win-x64 publish (installer\publish\win-x64).
      2. installer\msix\layout                    - the publish output without *.pdb and XML documentation files
                                                    (*.xml next to a same-named .dll/.exe), plus msix\Assets.
      3. AppxManifest.xml                         - installer\msix\AppxManifest.xml with Identity Name / Publisher /
                                                    Version / PublisherDisplayName / DisplayName substituted through
                                                    the XML DOM (so a Publisher with commas or quotes is escaped).
      4. makepri.exe                              - resources.pri built from a staging folder that holds ONLY the
                                                    manifest and Assets, so the .NET satellite folder "ru\" is not
                                                    mistaken for a language resource of the package. The generated
                                                    priconfig.xml loses its <packaging> section, otherwise makepri
                                                    splits the scale-125/150/200/400 tiles into resource packs that
                                                    the layout would not contain.
      5. makeappx.exe pack                        - the .msix.
      6. Optional -SignForSideload                - signs with a self-signed certificate whose Subject equals Publisher
                                                    (development placeholder Publisher only, unless -Force).
      7. Optional -RunWack                        - Windows App Certification Kit (needs an elevated shell).

    The version comes from the FILEVERSION resource of the published DesktopOrganizer.UI.exe (exactly where
    DesktopOrganizer.iss reads it) with revision required to be 0 (the Store reserves it; the script throws otherwise).

    IDENTITY. Name, Publisher, PublisherDisplayName and DisplayName MUST be copied character for character
    from Partner Center (Product management > Product identity / Store listing) for a Store submission. The
    defaults below are development placeholders: they produce a package that can be side-loaded but the Store
    rejects it. Values may also come from the environment variables DESKTOPORGANIZER_MSIX_IDENTITY_NAME,
    DESKTOPORGANIZER_MSIX_PUBLISHER, DESKTOPORGANIZER_MSIX_PUBLISHER_DISPLAY_NAME and
    DESKTOPORGANIZER_MSIX_DISPLAY_NAME (parameters win).
    The Store package is uploaded unsigned: the Store signs it.

.PARAMETER SdkBinDir
    Folder with makeappx.exe / makepri.exe / signtool.exe. Default: newest Windows Kits\10\bin\10.0.*\x64.

.PARAMETER SkipPublish
    Reuse the existing installer\publish\win-x64 instead of publishing again (local iteration only).

.PARAMETER Force
    Allows -SignForSideload together with a non-placeholder Publisher (normally refused: a package signed
    with a self-signed certificate that carries the real Store publisher subject must never be shipped).

.EXAMPLE
    .\installer\build-msix.ps1

.EXAMPLE
    .\installer\build-msix.ps1 -IdentityName "12345khagendev.DesktopOrganizer" `
        -Publisher "CN=AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" -PublisherDisplayName "khagendev"

.EXAMPLE
    .\installer\build-msix.ps1 -SignForSideload   # then follow the printed certificate-import instructions
#>
[CmdletBinding()]
param(
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$IdentityName = $(if ($env:DESKTOPORGANIZER_MSIX_IDENTITY_NAME) { $env:DESKTOPORGANIZER_MSIX_IDENTITY_NAME } else { "khagendev.DesktopOrganizer.Dev" }),
    [string]$Publisher = $(if ($env:DESKTOPORGANIZER_MSIX_PUBLISHER) { $env:DESKTOPORGANIZER_MSIX_PUBLISHER } else { "CN=DesktopOrganizer Dev" }),
    [string]$PublisherDisplayName = $(if ($env:DESKTOPORGANIZER_MSIX_PUBLISHER_DISPLAY_NAME) { $env:DESKTOPORGANIZER_MSIX_PUBLISHER_DISPLAY_NAME } else { "khagendev" }),
    [string]$DisplayName = $(if ($env:DESKTOPORGANIZER_MSIX_DISPLAY_NAME) { $env:DESKTOPORGANIZER_MSIX_DISPLAY_NAME } else { "DesktopOrganizer" }),
    [switch]$SignForSideload,
    [switch]$RunWack,
    [string]$SdkBinDir,
    [switch]$SkipPublish,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$placeholderIdentityName = "khagendev.DesktopOrganizer.Dev"
$placeholderPublisher = "CN=DesktopOrganizer Dev"
$msixDir = Join-Path $PSScriptRoot "msix"
$publishDir = Join-Path $PSScriptRoot "publish\win-x64"
$layoutDir = Join-Path $msixDir "layout"
$stagingDir = Join-Path $msixDir "staging"
$outputDir = Join-Path $PSScriptRoot "Output"
$assetsDir = Join-Path $msixDir "Assets"
$manifestTemplate = Join-Path $msixDir "AppxManifest.xml"
$manifestNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"
$uapNamespace = "http://schemas.microsoft.com/appx/manifest/uap/windows10"
$desktopNamespace = "http://schemas.microsoft.com/appx/manifest/desktop/windows10"
$sideloadFriendlyName = "DesktopOrganizer sideload"
$codeSigningEku = "1.3.6.1.5.5.7.3.3"

function Find-SdkBinDir {
    param([string]$Explicit)

    if ($Explicit) {
        foreach ($tool in "makeappx.exe", "makepri.exe") {
            if (-not (Test-Path (Join-Path $Explicit $tool))) {
                throw "$tool not found in -SdkBinDir '$Explicit'."
            }
        }
        return $Explicit
    }

    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $versions = Get-ChildItem -Path $root -Directory -Filter "10.0.*" |
            Where-Object { $_.Name -as [version] } |
            Sort-Object { [version]$_.Name } -Descending

        foreach ($versionDir in $versions) {
            $candidate = Join-Path $versionDir.FullName "x64"
            if ((Test-Path (Join-Path $candidate "makeappx.exe")) -and (Test-Path (Join-Path $candidate "makepri.exe"))) {
                return $candidate
            }
        }
    }

    throw "makeappx.exe / makepri.exe not found. Install the Windows 10/11 SDK (Windows Kits\10\bin\10.0.*\x64) or pass -SdkBinDir."
}

function Invoke-Native {
    param(
        [string]$Description,
        [string]$Tool,
        [string[]]$Arguments
    )

    Write-Host "  $Description"
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit code $LASTEXITCODE): $Tool"
    }
}

# True for the XML documentation file of an assembly (Foo.xml next to Foo.dll / Foo.exe); other XML files
# (configuration, data) are payload and stay in the package.
function Test-XmlDocumentationFile {
    param([System.IO.FileInfo]$File)

    if ($File.Extension -ne ".xml") {
        return $false
    }

    foreach ($assemblyExtension in ".dll", ".exe") {
        if (Test-Path -LiteralPath (Join-Path $File.DirectoryName ($File.BaseName + $assemblyExtension))) {
            return $true
        }
    }

    return $false
}

# True for a certificate this script created (or would have created) for the given Publisher: valid, with a
# private key, our friendly name, the same Subject (compared as a normalised distinguished name, not as a
# string) and the Code Signing enhanced key usage.
function Test-SideloadCertificate {
    param(
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        [string]$ExpectedSubject
    )

    if (-not $Certificate.HasPrivateKey -or $Certificate.NotAfter -le (Get-Date)) {
        return $false
    }

    if ($Certificate.FriendlyName -ne $sideloadFriendlyName) {
        return $false
    }

    $expectedName = New-Object System.Security.Cryptography.X509Certificates.X500DistinguishedName($ExpectedSubject)
    if ($Certificate.SubjectName.Name -ne $expectedName.Name) {
        return $false
    }

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ($oid.Value -eq $codeSigningEku) {
                    return $true
                }
            }
        }
    }

    return $false
}

# --- Identity sanity ---------------------------------------------------------------------------------

if ($IdentityName -eq $placeholderIdentityName -or $Publisher -eq $placeholderPublisher) {
    $placeholderMessage = ("Building with the DEVELOPMENT PLACEHOLDER identity ('$IdentityName' / '$Publisher'). " +
        "The package can be side-loaded but the Microsoft Store will reject it: for a Store submission pass " +
        "-IdentityName, -Publisher and -PublisherDisplayName copied character for character from Partner Center " +
        "(Product identity), or set DESKTOPORGANIZER_MSIX_IDENTITY_NAME / _PUBLISHER / _PUBLISHER_DISPLAY_NAME.")
    Write-Warning $placeholderMessage
    if ($env:GITHUB_ACTIONS) {
        # Surfaces as an annotation on the workflow run, where a plain warning line is easy to miss.
        Write-Host "::warning title=MSIX placeholder identity::$placeholderMessage"
    }
}

if ($SignForSideload -and $Publisher -ne $placeholderPublisher) {
    if (-not $Force) {
        throw ("-SignForSideload with Publisher '$Publisher' would create a self-signed certificate that carries a real Store " +
            "publisher Subject. Sideload-sign only with the development placeholder identity, or pass -Force if you really mean it. " +
            "Never ship such a package; the Store signs the Store package itself.")
    }

    Write-Warning "-SignForSideload -Force with a non-placeholder Publisher: the self-signed certificate will carry that Subject. Never ship such a package."
}

$sdkBin = Find-SdkBinDir -Explicit $SdkBinDir
$makeappx = Join-Path $sdkBin "makeappx.exe"
$makepri = Join-Path $sdkBin "makepri.exe"
Write-Host "Windows SDK tools: $sdkBin"

# --- 1. Publish --------------------------------------------------------------------------------------

if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir "DesktopOrganizer.UI.exe"))) {
        throw "-SkipPublish given but $publishDir has no DesktopOrganizer.UI.exe."
    }
    Write-Host "SkipPublish: reusing $publishDir"
}
else {
    Write-Host "Publishing (installer\build.ps1 -SkipInstaller)..."
    # build.ps1 is not written for StrictMode: run it in a child scope with strict mode switched off.
    & {
        Set-StrictMode -Off
        & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration -SkipInstaller
    }
}

if (-not (Test-Path (Join-Path $publishDir "DesktopOrganizer.UI.exe"))) {
    throw "Publish output $publishDir has no DesktopOrganizer.UI.exe."
}

# --- 2. Layout ---------------------------------------------------------------------------------------

if (-not (Test-Path $assetsDir) -or -not (Get-ChildItem -Path $assetsDir -Filter "*.png" -File)) {
    throw "No PNG assets in $assetsDir. Run installer\msix\Generate-Assets.ps1 and commit the result."
}

Write-Host "Building layout: $layoutDir"
if (Test-Path $layoutDir) { Remove-Item -Recurse -Force $layoutDir }
New-Item -ItemType Directory -Force -Path $layoutDir | Out-Null

$prefixLength = $publishDir.TrimEnd('\').Length + 1
Get-ChildItem -Path $publishDir -Recurse -File |
    Where-Object { $_.Extension -ne ".pdb" -and -not (Test-XmlDocumentationFile $_) } |
    ForEach-Object {
        $destination = Join-Path $layoutDir $_.FullName.Substring($prefixLength)
        $destinationDir = Split-Path -Parent $destination
        if (-not (Test-Path $destinationDir)) { New-Item -ItemType Directory -Force -Path $destinationDir | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }

$layoutAssets = Join-Path $layoutDir "Assets"
New-Item -ItemType Directory -Force -Path $layoutAssets | Out-Null
Copy-Item -Path (Join-Path $assetsDir "*.png") -Destination $layoutAssets

# --- 3. Version and manifest -------------------------------------------------------------------------

$versionInfo = (Get-Item (Join-Path $layoutDir "DesktopOrganizer.UI.exe")).VersionInfo
if ($versionInfo.FilePrivatePart -ne 0) {
    throw ("FILEVERSION of DesktopOrganizer.UI.exe has revision $($versionInfo.FilePrivatePart); the Store reserves the 4th section " +
        "and it must be 0. Use a three-part <Version> in Directory.Build.props.")
}

$version = "{0}.{1}.{2}.0" -f $versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart
if ($version -notmatch '^[1-9]\d*\.\d+\.\d+\.0$') {
    throw "FILEVERSION '$version' of DesktopOrganizer.UI.exe is not a valid Store version (major must be >= 1, revision 0). Check <Version> in Directory.Build.props."
}
$shortVersion = $version.Substring(0, $version.Length - 2)
Write-Host "Package version: $version"

$manifest = New-Object System.Xml.XmlDocument
$manifest.PreserveWhitespace = $true
$manifest.Load($manifestTemplate)

$namespaces = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
$namespaces.AddNamespace("f", $manifestNamespace)
$namespaces.AddNamespace("uap", $uapNamespace)
$namespaces.AddNamespace("desktop", $desktopNamespace)

$identityNode = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
$publisherDisplayNameNode = $manifest.SelectSingleNode("/f:Package/f:Properties/f:PublisherDisplayName", $namespaces)
$displayNameNode = $manifest.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", $namespaces)
$visualElementsNode = $manifest.SelectSingleNode("//uap:VisualElements", $namespaces)
$startupTaskNode = $manifest.SelectSingleNode("//desktop:StartupTask", $namespaces)
if ($null -eq $identityNode -or $null -eq $publisherDisplayNameNode -or $null -eq $displayNameNode -or
    $null -eq $visualElementsNode -or $null -eq $startupTaskNode) {
    throw "$manifestTemplate has no Identity, Properties/PublisherDisplayName, Properties/DisplayName, uap:VisualElements or desktop:StartupTask element."
}

$identityNode.SetAttribute("Name", $IdentityName)
$identityNode.SetAttribute("Publisher", $Publisher)
$identityNode.SetAttribute("Version", $version)
$publisherDisplayNameNode.InnerText = $PublisherDisplayName
$displayNameNode.InnerText = $DisplayName
$visualElementsNode.SetAttribute("DisplayName", $DisplayName)
$startupTaskNode.SetAttribute("DisplayName", $DisplayName)

# The template comment documents the $TOKEN$ placeholders; it must not leak into (or trip the check on) the output.
foreach ($comment in @($manifest.SelectNodes("//comment()"))) {
    [void]$comment.ParentNode.RemoveChild($comment)
}

if ($manifest.OuterXml -match '\$[A-Z_]+\$') {
    throw "Unsubstituted placeholder left in the manifest: $($Matches[0])"
}

$layoutManifest = Join-Path $layoutDir "AppxManifest.xml"
$manifest.Save($layoutManifest)

# --- 4. resources.pri --------------------------------------------------------------------------------

Write-Host "Building resources.pri"
if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
$stagingPackage = Join-Path $stagingDir "pkg"
New-Item -ItemType Directory -Force -Path (Join-Path $stagingPackage "Assets") | Out-Null
Copy-Item -Path $layoutManifest -Destination $stagingPackage
Copy-Item -Path (Join-Path $layoutAssets "*.png") -Destination (Join-Path $stagingPackage "Assets")

$priConfig = Join-Path $stagingDir "priconfig.xml"
$stagingPri = Join-Path $stagingDir "resources.pri"

# /dq en-US_ru-RU: underscore separates qualifiers, two Language values become the list "en-US;ru-RU".
Invoke-Native "makepri createconfig" $makepri @("createconfig", "/cf", $priConfig, "/dq", "en-US_ru-RU", "/pv", "10.0.0", "/o")

# createconfig writes a <packaging> section (autoResourcePackage for Language / Scale / DXFeatureLevel). With it
# makepri new splits the output into resources.pri + resources.scale-125/150/200/400.pri; only resources.pri is
# copied into the layout, so on HiDPI the tiles would silently fall back to scale-100. Drop the section: one
# self-contained resources.pri.
$priConfigXml = New-Object System.Xml.XmlDocument
$priConfigXml.Load($priConfig)
$packagingNode = $priConfigXml.SelectSingleNode("/resources/packaging")
if ($null -ne $packagingNode) {
    [void]$packagingNode.ParentNode.RemoveChild($packagingNode)
}
$priConfigXml.Save($priConfig)

Invoke-Native "makepri new" $makepri @("new", "/pr", $stagingPackage, "/cf", $priConfig, "/mn", $layoutManifest, "/of", $stagingPri, "/o")

$splitPriFiles = @(Get-ChildItem -Path $stagingDir -Filter "resources.*.pri" -File)
if ($splitPriFiles.Count -gt 0) {
    throw "makepri split resources.pri into resource packs ($($splitPriFiles.Name -join ', ')); the layout would ship only the main file."
}

Copy-Item -Path $stagingPri -Destination (Join-Path $layoutDir "resources.pri") -Force

# --- 5. Pack -----------------------------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$msixPath = Join-Path $outputDir "DesktopOrganizer-$shortVersion.msix"
Write-Host "Packing: $msixPath"
Invoke-Native "makeappx pack" $makeappx @("pack", "/d", $layoutDir, "/p", $msixPath, "/o")

# --- 6. Sideload signing (optional) ------------------------------------------------------------------

if ($SignForSideload) {
    $signtool = Join-Path $sdkBin "signtool.exe"
    if (-not (Test-Path $signtool)) {
        throw "signtool.exe not found in $sdkBin."
    }

    $certificate = Get-ChildItem -Path "Cert:\CurrentUser\My" |
        Where-Object { Test-SideloadCertificate -Certificate $_ -ExpectedSubject $Publisher } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $certificate) {
        Write-Host "Creating a self-signed sideload certificate for '$Publisher' in Cert:\CurrentUser\My"
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Publisher `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy NonExportable `
            -NotAfter (Get-Date).AddYears(1) `
            -FriendlyName $sideloadFriendlyName `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}$codeSigningEku", "2.5.29.19={text}")
    }

    Invoke-Native "signtool sign" $signtool @("sign", "/fd", "SHA256", "/sha1", $certificate.Thumbprint, $msixPath)

    $cerPath = Join-Path $outputDir "DesktopOrganizer-sideload.cer"
    [void](Export-Certificate -Cert $certificate -FilePath $cerPath -Force)

    Write-Host ""
    Write-Host "Signed for sideload with a self-signed certificate (development only, never ship it)."
    Write-Host "To install, trust the certificate once from an ELEVATED PowerShell (this script does not import it):"
    Write-Host "  Import-Certificate -FilePath '$cerPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Write-Host "  Add-AppxPackage -Path '$msixPath'"
    Write-Host "Remove the certificate when done (installer\README.md, the sideload-certificate removal section):"
    Write-Host "  Remove-Item Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)   # elevated shell"
    Write-Host "  Remove-Item Cert:\CurrentUser\My\$($certificate.Thumbprint)"
}

# --- 7. WACK (optional) ------------------------------------------------------------------------------

if ($RunWack) {
    $appcert = $null
    $fromPath = Get-Command appcert.exe -ErrorAction SilentlyContinue
    if ($fromPath) {
        $appcert = $fromPath.Source
    }
    else {
        $kitRoots = @("${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit", "${env:ProgramFiles}\Windows Kits\10\App Certification Kit")
        $appcert = $kitRoots | ForEach-Object { Join-Path $_ "appcert.exe" } | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $appcert) {
        Write-Warning "appcert.exe (Windows App Certification Kit) not found - skipping -RunWack. Install the Windows SDK 'Windows App Certification Kit' component."
    }
    else {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            Write-Warning "WACK needs an elevated shell; this one is not - the run will probably fail."
        }
        if (-not $SignForSideload) {
            Write-Warning "WACK installs the package to test it; an unsigned package may fail to install. Combine -RunWack with -SignForSideload (and trust the certificate first)."
        }

        $reportPath = Join-Path $outputDir "wack-report.xml"
        Invoke-Native "appcert reset" $appcert @("reset")
        Invoke-Native "appcert test" $appcert @("test", "-appxpackagepath", $msixPath, "-reportoutputpath", $reportPath)
        Write-Host "WACK report: $reportPath (look for OVERALL_RESULT=PASS)"
    }
}

# --- Done --------------------------------------------------------------------------------------------

$msixInfo = Get-Item $msixPath
Write-Host ""
Write-Host ("Done: {0} ({1:N1} MB)" -f $msixInfo.FullName, ($msixInfo.Length / 1MB))
if (-not $SignForSideload) {
    Write-Host "The package is unsigned: fine for upload to Partner Center (the Store signs it); use -SignForSideload to install it locally."
}
