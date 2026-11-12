param([Parameter(Mandatory)][string]$Release)

$ErrorActionPreference = 'Stop'
$releasePath = (Resolve-Path -LiteralPath $Release).Path
$metadata = Get-Content -LiteralPath (Join-Path $releasePath 'release.json') -Raw | ConvertFrom-Json
$version = $metadata.version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$' -or
    $metadata.tag -cne "v$version" -or $metadata.prerelease -ne $version.Contains('-')) {
    throw 'Invalid package release metadata.'
}
$id = if ($metadata.prerelease) { 'Zkvr.PaperDesk.Preview' } else { 'Zkvr.PaperDesk' }
$repo = 'https://github.com/Zkvr/PaperDesk'
$installer = 'PaperDesk-Setup-win-x64.exe'
$file = Get-Item -LiteralPath (Join-Path $releasePath $installer)
$hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
$checksums = Get-Content -LiteralPath (Join-Path $releasePath 'SHA256SUMS.txt')
if ($file.VersionInfo.ProductVersion.TrimEnd() -cne $version -or $checksums -notcontains "$hash  $installer") {
    throw 'The installer version or SHA-256 does not match the release.'
}
$setup = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../installer/PaperDesk.iss') -Raw
if ($setup -notmatch '(?m)^AppId=\{(\{[\da-fA-F-]{36}\})\s*$') { throw 'Could not read the Inno Setup AppId.' }
$productCode = $Matches[1] + '_is1'
$packagePath = $id.Replace('.', '/')
$directory = Join-Path $releasePath "winget/manifests/z/$packagePath/$version"
New-Item -ItemType Directory -Path $directory -Force | Out-Null

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: '$version'
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content -LiteralPath (Join-Path $directory "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: '$version'
InstallerType: inno
Scope: user
MinimumOSVersion: 10.0.19041.0
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
Installers:
- Architecture: x64
  InstallerUrl: $repo/releases/download/$($metadata.tag)/$installer
  InstallerSha256: $hash
  ProductCode: '$productCode'
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content -LiteralPath (Join-Path $directory "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $id
PackageVersion: '$version'
PackageLocale: en-US
Publisher: PaperDesk
PublisherUrl: $repo
PackageName: PaperDesk
PackageUrl: $repo
License: All rights reserved
ShortDescription: Offline desktop sticky notes with checklists, images and stacks.
ReleaseNotesUrl: $repo/releases/tag/$($metadata.tag)
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content -LiteralPath (Join-Path $directory "$id.locale.en-US.yaml") -Encoding utf8

$directory
