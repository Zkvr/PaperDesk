param(
    [string]$Compiler,
    [switch]$PortableOnly,
    [string]$Output = 'artifacts\release',
    [string]$ExpectedTag
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'PaperDesk.Desktop\PaperDesk.Desktop.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Expected a Windows package version in the desktop project, for example 0.7.0-rc.'
}
$tag = "v$version"
if ($ExpectedTag -and $ExpectedTag -cne $tag) { throw "Tag $ExpectedTag does not match $tag in the desktop project." }
$numericVersion = ($version -split '-', 2)[0] + '.0'
if (!$PortableOnly) {
    if (!$Compiler) { throw 'Specify the Inno Setup compiler or use -PortableOnly.' }
    $Compiler = (Resolve-Path -LiteralPath $Compiler).Path
}
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $root $Output)) }
$artifacts = Join-Path $root 'artifacts'
$artifactDirectory = New-Item -ItemType Directory -Path $artifacts -Force
if ($artifactDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected artifacts path.' }
$build = (New-Item -ItemType Directory -Path (Join-Path $artifacts ('stage-' + [Guid]::NewGuid().ToString('N')))).FullName
$stage = Join-Path $build 'publish'
$productsDir = Join-Path $build 'packages'
$sdk = Join-Path $root '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $sdk) { . "$PSScriptRoot\env.ps1" }
else { $sdk = 'dotnet' }

try {
    New-Item -ItemType Directory -Path $productsDir | Out-Null
    $publishArgs = @($project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', (Join-Path $build 'msbuild'), '-o', $stage, '-m:1',
        '-p:UseSharedCompilation=false', '-p:PaperDeskLocalOnly=true', '-p:DebugType=None',
        '-p:IncludeSourceRevisionInInformationalVersion=false', "-p:PathMap=$root=/_/")
    & $sdk publish @publishArgs
    if ($LASTEXITCODE) { throw "Windows publish failed ($LASTEXITCODE)" }
    if ((Get-Item -LiteralPath "$stage\PaperDesk.exe").VersionInfo.ProductVersion -cne $version) {
        throw "The published executable version does not match $version."
    }
    Copy-Item -LiteralPath "$root\licenses" -Destination "$stage\licenses" -Recurse

    $portable = Join-Path $productsDir 'PaperDesk-Portable-win-x64.zip'
    $products = @($portable)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $files = [IO.Directory]::GetFiles($stage, '*', [IO.SearchOption]::AllDirectories)
    [Array]::Sort($files, [StringComparer]::Ordinal)
    $stream = [IO.File]::Open($portable, [IO.FileMode]::Create)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in $files) {
                $name = $file.Substring($stage.Length + 1).Replace('\', '/')
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $source = [IO.File]::OpenRead($file)
                try {
                    $destination = $entry.Open()
                    try { $source.CopyTo($destination) }
                    finally { $destination.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }

    if (!$PortableOnly) {
        $installer = Join-Path $productsDir 'PaperDesk-Setup-win-x64.exe'
        & $Compiler /Qp "/DBuildDir=$stage" "/DOutputPath=$productsDir" "/DAppVersion=$version" "/DAppNumericVersion=$numericVersion" "$root\installer\PaperDesk.iss"
        if ($LASTEXITCODE -or !(Test-Path -LiteralPath $installer)) { throw "Installer build failed ($LASTEXITCODE)" }
        if ((Get-Item -LiteralPath $installer).VersionInfo.ProductVersion.TrimEnd() -cne $version) {
            throw "The installer version does not match $version."
        }
        $products += $installer
    }

    $checksums = Join-Path $productsDir 'SHA256SUMS.txt'
    Get-FileHash -LiteralPath $products -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
        Set-Content -LiteralPath $checksums -Encoding ascii
    $metadata = Join-Path $productsDir 'release.json'
    [ordered]@{ version = $version; tag = $tag; prerelease = $version.Contains('-') } |
        ConvertTo-Json | Set-Content -LiteralPath $metadata -Encoding utf8
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    foreach ($product in ($products + $metadata + $checksums)) {
        Move-Item -LiteralPath $product -Destination $outputPath -Force
    }
    Get-Item -LiteralPath @($products | ForEach-Object { Join-Path $outputPath ([IO.Path]::GetFileName($_)) })
} finally {
    $resolved = Get-Item -LiteralPath $build -Force
    if ($resolved.Parent.FullName -ne $artifacts -or ($resolved.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected package staging path.'
    }
    Remove-Item -LiteralPath $resolved.FullName -Recurse -Force
}
