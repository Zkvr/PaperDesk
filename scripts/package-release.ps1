param(
    [Parameter(Mandatory)][string]$Compiler,
    [string]$Output = "artifacts\release",
    [string]$Version = "0.3.0-preview.17"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$outputPath = if ([IO.Path]::IsPathRooted($Output)) { [IO.Path]::GetFullPath($Output) } else { [IO.Path]::GetFullPath((Join-Path $root $Output)) }
$build = Join-Path $root ('artifacts\stage-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $build 'publish'
$sdk = Join-Path $root '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $sdk) { . "$PSScriptRoot\env.ps1" }
else { $sdk = 'dotnet' }

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
& $sdk publish "$root\PaperDesk.Desktop\PaperDesk.Desktop.csproj" -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $build 'msbuild') -o $stage -m:1 "-p:UseSharedCompilation=false" "-p:Version=$Version" "-p:PaperDeskLocalOnly=true"
if ($LASTEXITCODE) { throw "Windows publish failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath "$root\licenses" -Destination "$stage\licenses" -Recurse

$portable = Join-Path $outputPath 'PaperDesk-Portable-win-x64.zip'
$installer = Join-Path $outputPath 'PaperDesk-Setup-win-x64.exe'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -Force
& $Compiler /Qp "/DBuildDir=$stage" "/DOutputPath=$outputPath" "/DAppVersion=$Version" "$root\installer\PaperDesk.iss"
if ($LASTEXITCODE -or !(Test-Path -LiteralPath $installer)) { throw "Installer build failed ($LASTEXITCODE)" }

Get-FileHash -LiteralPath $portable,$installer -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content -LiteralPath (Join-Path $outputPath 'SHA256SUMS.txt') -Encoding ascii
Get-Item -LiteralPath $portable,$installer
